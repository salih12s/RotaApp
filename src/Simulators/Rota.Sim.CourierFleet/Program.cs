using System.Diagnostics;
using System.Globalization;
using Rota.Sim.CourierFleet;

FleetOptions options;
try
{
    options = FleetOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Usage: --couriers 200 --interval 3 [--url http://localhost:5203] [--duration 60] [--disconnect-rate 0.01] [--reorder-rate 0.05] [--seed 1] [--probe-nearby]");
    Console.Error.WriteLine("Offers (v2): --offers [--dispatch-grpc http://localhost:5202] [--dispatch-http http://localhost:5102] [--accept-rate 0.6] [--reject-rate 0.25] [--response-delay 5]");
    return 2;
}

Console.WriteLine(FormattableString.Invariant(
    $"Fleet: {options.Couriers} couriers, one point every {options.IntervalSeconds} s each (~{options.Couriers / options.IntervalSeconds:0.#} points/s), to {options.Url}"));

var stats = new FleetStats();
var runner = new FleetRunner(options, stats, TimeProvider.System);
using var stop = new CancellationTokenSource(options.DurationSeconds > 0 ? TimeSpan.FromSeconds(options.DurationSeconds) : Timeout.InfiniteTimeSpan);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the couriers finish their streams cleanly
    stop.Cancel();
};

var clock = Stopwatch.StartNew();
var reporter = Task.Run(async () =>
{
    var last = (Sent: 0L, At: TimeSpan.Zero);
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
    try
    {
        while (await timer.WaitForNextTickAsync(stop.Token))
        {
            var sent = stats.Sent;
            var now = clock.Elapsed;
            var rate = (sent - last.Sent) / (now - last.At).TotalSeconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{now:mm\\:ss}] sent {sent} ({rate:0.#}/s now) · offline events {stats.Disconnects} · errors {stats.StreamErrors}"));
            last = (sent, now);
        }
    }
    catch (OperationCanceledException)
    {
        // Run over.
    }
});

await runner.RunAsync(stop.Token);
await reporter;

var seconds = clock.Elapsed.TotalSeconds;
Console.WriteLine();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"Done in {seconds:0.#} s: sent {stats.Sent} points = {stats.Sent / seconds:0.#} points/s on average."));
Console.WriteLine($"Server answers at stream end: accepted {stats.Accepted}, not newer {stats.OutOfOrder}; stream errors {stats.StreamErrors}; offline episodes {stats.Disconnects}.");
if (options.Offers)
{
    Console.WriteLine($"Offers: seen {stats.OffersSeen}, accepted {stats.OffersAccepted}, rejected {stats.OffersRejected}, ignored {stats.OffersIgnored}, withdrawn {stats.OffersWithdrawn}; "
        + $"answers refused (too late) {stats.AnswersRefused}, failed {stats.AnswersFailed}; offer stream errors {stats.OfferStreamErrors}.");
}

if (stats.ProbeSummary() is { } probe)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"GetNearbyCouriers over {probe.Count} calls: p50 {probe.P50:0.0} ms, p95 {probe.P95:0.0} ms, p99 {probe.P99:0.0} ms, max {probe.Max:0.0} ms (errors {stats.ProbeErrors})."));
}

return 0;
