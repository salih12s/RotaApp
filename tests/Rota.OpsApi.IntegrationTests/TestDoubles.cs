using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Rota.TestKit;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>A request the fake saw, kept so a test can ask "what exactly did the Ops API send?".</summary>
public sealed record SeenRequest(HttpMethod Method, string Path, string Body, string? DevUser);

/// <summary>
/// A stub for Dispatch (and for Orders' JWKS): answers with whatever the test put in <see cref="Respond"/> and remembers every request.
/// It sits where the real network would be, under the Ops API's own timeout and circuit breaker, so those are exercised for real.
/// </summary>
public sealed class FakeHttpServer : HttpMessageHandler
{
    private readonly List<SeenRequest> _seen = [];

    /// <summary>Gets the request's cancellation token like a real network handler would: a slow answer must stop when the caller's timeout fires.</summary>
    public Func<SeenRequest, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    public IReadOnlyList<SeenRequest> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>The body Dispatch's <c>ProblemResults</c> writes (RFC 9457), with the catalog's type URI.</summary>
    public static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail) =>
        Json(status, JsonSerializer.Serialize(new { type = $"https://rota.dev/errors/{code}", title = code, status = (int)status, detail }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var devUser = request.Headers.TryGetValues(DevUsers.HeaderName, out var values) ? values.Single() : null;
        var seen = new SeenRequest(request.Method, request.RequestUri!.AbsolutePath, body, devUser);
        lock (_seen)
        {
            _seen.Add(seen);
        }

        return await Respond(seen, cancellationToken);
    }
}

/// <summary>
/// Plays Orders' signing side: its own EC P-256 key, the JWKS document Orders would serve, and tokens built the way <c>TrackingTokenIssuer</c> builds them
/// (ES256, issuer <c>rota-orders</c>, audience <c>rota-tracking</c>, claim <c>order_id</c>).
/// </summary>
public sealed class TrackingKeys : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>The JWKS body (public half only).</summary>
    public string Jwks()
    {
        using var publicOnly = ECDsa.Create(_key.ExportParameters(includePrivateParameters: false));
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicOnly));
        var kid = Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "EC", crv = "P-256", x = jwk.X, y = jwk.Y, kid, use = "sig", alg = "ES256" } },
        });
    }

    public string Token(Guid orderId, TimeSpan? lifetime = null, string audience = "rota-tracking", string issuer = "rota-orders")
    {
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(new ECDsaSecurityKey(_key) { KeyId = Kid() }, SecurityAlgorithms.EcdsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory(),
        };
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now + (lifetime ?? TimeSpan.FromHours(1)),
            Claims = new Dictionary<string, object> { ["order_id"] = orderId.ToString("D") },
            SigningCredentials = credentials,
        });
    }

    public void Dispose() => _key.Dispose();

    private string Kid()
    {
        using var publicOnly = ECDsa.Create(_key.ExportParameters(includePrivateParameters: false));
        return Base64UrlEncoder.Encode(JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicOnly)).ComputeJwkThumbprint());
    }
}

/// <summary>
/// A GraphQL-over-WebSocket client (the <c>graphql-transport-ws</c> protocol): connect, subscribe, then read what the server pushes.
/// A subscription is not one request and one answer: the connection stays open and each event arrives as its own <c>next</c> message.
/// </summary>
public sealed class GraphQlSocket : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private int _lastId;

    private GraphQlSocket(WebSocket socket) => _socket = socket;

    public static async Task<GraphQlSocket> ConnectAsync(OpsApiFactory api, string? devUser, CancellationToken cancellationToken)
    {
        var client = api.Server.CreateWebSocketClient();
        client.SubProtocols.Add("graphql-transport-ws");
        client.ConfigureRequest = request =>
        {
            if (devUser is not null)
            {
                request.Headers[DevUsers.HeaderName] = devUser;
            }
        };

        var socket = new GraphQlSocket(await client.ConnectAsync(new Uri("ws://localhost/graphql"), cancellationToken));
        await socket.SendAsync(new { type = "connection_init" }, cancellationToken);
        var ack = await socket.ReadAsync(cancellationToken);
        ack.GetProperty("type").GetString().ShouldBe("connection_ack");
        return socket;
    }

    public Task SubscribeAsync(string query, object? variables, CancellationToken cancellationToken) =>
        SendAsync(new { id = Interlocked.Increment(ref _lastId).ToString(System.Globalization.CultureInfo.InvariantCulture), type = "subscribe", payload = new { query, variables } }, cancellationToken);

    /// <summary>The next <c>next</c>, <c>error</c> or <c>complete</c> message; keep-alive pings are skipped. Times out instead of hanging a test.</summary>
    public async Task<JsonElement> NextAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var message = await ReadAsync(limit.Token);
                if (message.GetProperty("type").GetString() is "next" or "error" or "complete")
                {
                    return message;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No subscription message within {timeout.TotalSeconds:0.#}s.");
        }
    }

    /// <summary>
    /// Waits for the next message while calling <paramref name="trigger"/> every 100 ms. A subscription registers a moment after it was sent (there is no
    /// "subscribed" acknowledgement in the protocol), so one trigger fired too early would be lost; repeating it is how the test avoids a fixed sleep.
    /// </summary>
    public async Task<JsonElement> NextWhileAsync(Func<Task> trigger, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var next = NextAsync(timeout, cancellationToken);
        while (!next.IsCompleted)
        {
            await trigger();
            await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken));
        }

        return await next;
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // The server may already be gone; closing is courtesy, not a check.
            }
        }

        _socket.Dispose();
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken) =>
        await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    private async Task<JsonElement> ReadAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException($"The server closed the socket: {result.CloseStatus} {result.CloseStatusDescription}");
            }

            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonSerializer.Deserialize<JsonElement>(message.ToArray());
    }
}
