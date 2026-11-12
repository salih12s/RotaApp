using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rota.Sim.Merchant;

public enum VerifyOutcome
{
    Valid,
    MissingOrMalformedHeader,
    TooOld,
    BadSignature,
}

/// <summary>
/// How a merchant checks a Rota webhook (docs/05 §4), written independently of the sending side on purpose: if both sides were the
/// same code, a mistake in the format would never show. This is also the example for the README.
/// </summary>
public static class WebhookVerifier
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// <paramref name="signatureHeader"/> is <c>t=1762857065,v1=5f2b…</c>; <paramref name="body"/> is the raw request body exactly as received
    /// (not re-serialised: a re-serialised JSON can differ in spacing and break the signature).
    /// </summary>
    public static VerifyOutcome Verify(string secret, string? signatureHeader, string body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(body);

        string? t = null, v1 = null;
        foreach (var part in (signatureHeader ?? string.Empty).Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2)
            {
                if (pair[0] == "t")
                {
                    t = pair[1];
                }
                else if (pair[0] == "v1")
                {
                    v1 = pair[1];
                }
            }
        }

        if (t is null || v1 is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return VerifyOutcome.MissingOrMalformedHeader;
        }

        // Replay protection: a captured request is only good for five minutes. The timestamp is inside the signed text, so it cannot be refreshed.
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).TotalSeconds) > Tolerance.TotalSeconds)
        {
            return VerifyOutcome.TooOld;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        byte[] given;
        try
        {
            given = Convert.FromHexString(v1);
        }
        catch (FormatException)
        {
            return VerifyOutcome.BadSignature;
        }

        // Constant-time comparison: "bytes equal so far" must not be measurable.
        return CryptographicOperations.FixedTimeEquals(expected, given) ? VerifyOutcome.Valid : VerifyOutcome.BadSignature;
    }
}

/// <summary>Settings of <c>receive</c> mode: <c>--port 5301 --secret whsec_… --error-rate 0.2 --delay-ms 0</c>.</summary>
public sealed record ReceiverOptions(int Port, string Secret, double ErrorRate, int DelayMilliseconds, int Seed)
{
    public static ReceiverOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int port = 5301, delay = 0, seed = 1;
        var secret = string.Empty;
        double errorRate = 0;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            var value = args[i + 1];
            switch (args[i])
            {
                case "--port": port = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--secret": secret = value; break;
                case "--error-rate": errorRate = double.Parse(value, CultureInfo.InvariantCulture); break;
                case "--delay-ms": delay = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(value, CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (secret.Length == 0 || errorRate is < 0 or > 1 || delay < 0)
        {
            throw new ArgumentException("--secret is required, --error-rate must be within [0, 1], --delay-ms >= 0.");
        }

        return new ReceiverOptions(port, secret, errorRate, delay, seed);
    }
}

/// <summary>
/// A pretend merchant's webhook endpoint: checks the signature, drops repeats by <c>id</c>, drops out-of-date versions by <c>orderVersion</c>,
/// and on purpose fails some answers (500) or answers slowly, so Rota's retry, circuit breaker and bulkhead can be watched at work.
/// </summary>
public sealed class WebhookReceiver(ReceiverOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, byte> _seen = new();
    private readonly ConcurrentDictionary<Guid, int> _latestVersion = new();
    private readonly Random _random = new(options.Seed);
    private long _accepted, _duplicates, _stale, _rejectedSignature, _injectedFailures;

    public string Summary =>
        $"Webhooks: accepted {Interlocked.Read(ref _accepted)} new, {Interlocked.Read(ref _duplicates)} repeats dropped by id, "
        + $"{Interlocked.Read(ref _stale)} older versions dropped, {Interlocked.Read(ref _rejectedSignature)} rejected for a bad signature, "
        + $"{Interlocked.Read(ref _injectedFailures)} failed on purpose.";

    /// <summary>The decision for one request, separate from the listener so it can be tested without a socket.</summary>
    public (int Status, string Note) Handle(string? signatureHeader, string body)
    {
        var verdict = WebhookVerifier.Verify(options.Secret, signatureHeader, body, time.GetUtcNow());
        if (verdict != VerifyOutcome.Valid)
        {
            Interlocked.Increment(ref _rejectedSignature);
            return (401, verdict.ToString());
        }

        bool fail;
        lock (_random)
        {
            fail = _random.NextDouble() < options.ErrorRate;
        }

        if (fail)
        {
            Interlocked.Increment(ref _injectedFailures);
            return (500, "injected failure");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var id = root.GetProperty("id").GetGuid();
        var data = root.GetProperty("data");
        var orderId = data.GetProperty("orderId").GetGuid();
        var version = data.GetProperty("orderVersion").GetInt32();

        if (!_seen.TryAdd(id, 0))
        {
            Interlocked.Increment(ref _duplicates); // at-least-once: the same id can come again; answer 2xx and do nothing
            return (200, "duplicate");
        }

        var latest = _latestVersion.AddOrUpdate(orderId, version, (_, current) => Math.Max(current, version));
        if (version < latest)
        {
            Interlocked.Increment(ref _stale); // there is no ordering guarantee: an older version that arrives late is ignored
            return (200, "stale");
        }

        Interlocked.Increment(ref _accepted);
        return (200, "accepted");
    }

    public async Task RunAsync(CancellationToken stop)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{options.Port}/");
        listener.Start();
        Console.WriteLine($"Webhook receiver on http://localhost:{options.Port}/ (error rate {options.ErrorRate}, delay {options.DelayMilliseconds} ms). Ctrl+C to stop.");
        using var registration = stop.Register(listener.Stop);

        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(stop);
                if (options.DelayMilliseconds > 0)
                {
                    await Task.Delay(options.DelayMilliseconds, stop);
                }

                var (status, _) = Handle(context.Request.Headers["Rota-Signature"], body);
                context.Response.StatusCode = status;
                context.Response.Close();
            }, stop);
        }

        Console.WriteLine(Summary);
    }
}
