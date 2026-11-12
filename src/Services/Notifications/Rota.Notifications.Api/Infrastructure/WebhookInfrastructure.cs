using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Rota.BuildingBlocks.Primitives;
using Rota.Notifications.Api.Domain;

namespace Rota.Notifications.Api.Infrastructure;

/// <summary>Creates webhook secrets and keeps them encrypted at rest with ASP.NET Data Protection (docs/06 §4).</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Rota.Notifications.WebhookSecret.v1");

    /// <summary>A new random secret: 32 bytes from the system's CSPRNG, URL-safe, with a prefix that says what it is.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string Protect(string secret) => _protector.Protect(secret);

    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
}

/// <summary>Checks a merchant's URL when it is saved: the syntax rules, then every address the host name resolves to (SSRF, docs/06 §3).</summary>
public sealed class WebhookUrlValidator(IOptions<NotificationsOptions> options)
{
    public async Task<Result<Uri>> ValidateAsync(string? candidate, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var syntax = WebhookUrlPolicy.Validate(candidate, settings.AllowHttp, settings.AllowPrivateNetworks);
        if (syntax.IsFailure || settings.AllowPrivateNetworks || IPAddress.TryParse(syntax.Value.Host.Trim('[', ']'), out _))
        {
            return syntax;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(syntax.Value.Host, cancellationToken);
            if (addresses.Length == 0)
            {
                return WebhookUrlPolicy.InvalidUrl("url host cannot be resolved.");
            }

            // One internal address among the answers is enough to refuse: the connection could pick that one.
            return addresses.Any(WebhookUrlPolicy.IsForbidden)
                ? WebhookUrlPolicy.InvalidUrl("url must not point to a private or internal address.")
                : syntax;
        }
        catch (SocketException)
        {
            return WebhookUrlPolicy.InvalidUrl("url host cannot be resolved.");
        }
    }
}

/// <summary>What one delivery attempt came to.</summary>
public sealed record SendResult(bool Success, int? StatusCode, string? Error);

/// <summary>
/// Sends one signed webhook. The HTTP client refuses redirects (a redirect could lead somewhere internal) and checks the address it
/// is about to connect to, not only the one it saw when the URL was saved.
/// </summary>
public sealed class WebhookSender(HttpClient http, IOptions<NotificationsOptions> options, TimeProvider time)
{
    /// <summary>The handler every sender uses: no redirects, and a connect step that vets the resolved address.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var allowed = addresses.Where(a => allowPrivateNetworks || !WebhookUrlPolicy.IsForbidden(a)).ToArray();
            if (allowed.Length == 0)
            {
                throw new HttpRequestException("The webhook host resolves only to private or internal addresses; the connection was refused.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public async Task<SendResult> SendAsync(string url, string secret, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(delivery.Payload, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Add("Rota-Event-Id", delivery.EventId.ToString("D"));
        request.Headers.Add("Rota-Event-Type", delivery.EventType);
        request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Header(secret, time.GetUtcNow(), delivery.Payload));

        // Own timer, not HttpClient.Timeout: the failure must say "timeout" instead of an anonymous cancellation.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(options.Value.DeliveryTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            var code = (int)response.StatusCode;
            return response.IsSuccessStatusCode ? new SendResult(true, code, null) : new SendResult(false, code, $"The receiver answered {code}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SendResult(false, null, $"Timeout: no answer within {options.Value.DeliveryTimeout.TotalSeconds:0.#} s.");
        }
        catch (HttpRequestException ex)
        {
            return new SendResult(false, null, ex.InnerException?.Message ?? ex.Message);
        }
    }
}

/// <summary>Per-merchant state of the worker: the bulkhead (requests in flight) and the circuit breaker. In memory, per instance.</summary>
public sealed class MerchantGates(IOptions<NotificationsOptions> options)
{
    public sealed record Gate(SemaphoreSlim Slots, MerchantCircuit Circuit);

    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    public Gate For(string merchantId) => _gates.GetOrAdd(merchantId, _ => new Gate(
        new SemaphoreSlim(options.Value.MerchantConcurrency),
        new MerchantCircuit(options.Value.CircuitThreshold, options.Value.CircuitOpenFor)));
}
