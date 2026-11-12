using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Rota.Sim.Merchant;

MerchantOptions options;
try
{
    options = MerchantOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
{
    await Console.Error.WriteLineAsync(ex.Message);
    await Console.Error.WriteLineAsync("Usage: --orders 50 [--merchants 3] [--url http://localhost:5101] [--interval 0.5] [--codes-file codes.jsonl] [--cancel-rate 0.1] [--seed 1] [--timeout 300] [--no-wait]");
    return 2;
}

string[] finalStatuses = ["Delivered", "Returned", "Cancelled"];
var random = new Random(options.Seed);
using var http = new HttpClient { BaseAddress = options.Url };
var created = new List<(Guid Id, string Merchant)>();
var failedToCreate = 0;
var cancelRequests = new List<Task<HttpStatusCode?>>();
var clock = Stopwatch.StartNew();

Console.WriteLine(FormattableString.Invariant(
    $"Merchants: {options.Orders} orders from {options.Merchants} merchants, one every {options.IntervalSeconds} s, to {options.Url}; codes go to {options.CodesFile}"));

for (var i = 0; i < options.Orders; i++)
{
    var merchantIndex = i % options.Merchants;
    var merchant = $"merchant-sim-{merchantIndex}";
    var body = OrderFactory.Create(random, merchantIndex, i);

    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(body) };
    request.Headers.Add("X-Dev-User", $"merchant:{merchant}");
    request.Headers.Add("Idempotency-Key", $"sim-{options.Seed}-{i}-{Guid.NewGuid():N}");

    try
    {
        using var response = await http.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            failedToCreate++;
            await Console.Error.WriteLineAsync($"Order {i} was answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            continue;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = json.GetProperty("id").GetGuid();
        var code = json.GetProperty("deliveryCode").GetString()!;

        // The merchant tells the customer; the customer tells the courier. A line in a file stands in for that chain.
        // Written the moment the order exists, long before any courier is near: the order is not even assigned yet.
        await File.AppendAllTextAsync(options.CodesFile, JsonSerializer.Serialize(new { orderId, code }) + Environment.NewLine);
        created.Add((orderId, merchant));

        if (random.NextDouble() < options.CancelRate)
        {
            var delay = TimeSpan.FromSeconds(1 + (random.NextDouble() * 5));
            cancelRequests.Add(CancelLaterAsync(http, orderId, merchant, delay));
        }
    }
    catch (HttpRequestException ex)
    {
        failedToCreate++;
        await Console.Error.WriteLineAsync($"Order {i} could not be sent ({ex.Message}); is Orders running at {options.Url}?");
    }

    await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds));
}

Console.WriteLine($"Created {created.Count} orders ({failedToCreate} failed) in {clock.Elapsed.TotalSeconds:0.#} s.");

if (options.NoWait)
{
    return failedToCreate == 0 ? 0 : 1;
}

// Wait for every order to reach a final status; whatever does not is "stuck" and is named.
var deadline = TimeSpan.FromSeconds(options.TimeoutSeconds) + clock.Elapsed;
var outcomes = await Task.WhenAll(created.Select(o => WaitForFinalAsync(http, o.Id, o.Merchant, finalStatuses, deadline, clock)));
var cancelAnswers = await Task.WhenAll(cancelRequests);

Console.WriteLine();
Console.WriteLine("Final statuses:");
foreach (var group in outcomes.GroupBy(o => o).OrderBy(g => g.Key, StringComparer.Ordinal))
{
    Console.WriteLine($"  {group.Key,-14} {group.Count()}");
}

var cancelled = cancelAnswers.Count(a => a == HttpStatusCode.OK);
var tooLate = cancelAnswers.Count(a => a == HttpStatusCode.Conflict);
Console.WriteLine($"Cancel requests: {cancelAnswers.Length} sent, {cancelled} accepted, {tooLate} too late (409).");

var stuck = created.Zip(outcomes).Where(p => !finalStatuses.Contains(p.Second, StringComparer.Ordinal)).ToList();
Console.WriteLine(stuck.Count == 0 ? "No order is stuck." : $"STUCK: {stuck.Count} orders did not reach a final status within {options.TimeoutSeconds} s:");
foreach (var (order, status) in stuck.Take(20))
{
    Console.WriteLine($"  {order.Id} still {status}");
}

return stuck.Count == 0 && failedToCreate == 0 ? 0 : 1;

static async Task<HttpStatusCode?> CancelLaterAsync(HttpClient http, Guid orderId, string merchant, TimeSpan delay)
{
    await Task.Delay(delay);
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/cancel") { Content = JsonContent.Create(new { reason = "customer changed mind (simulated)" }) };
    request.Headers.Add("X-Dev-User", $"merchant:{merchant}");
    try
    {
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }
    catch (HttpRequestException)
    {
        return null;
    }
}

static async Task<string> WaitForFinalAsync(HttpClient http, Guid orderId, string merchant, string[] finalStatuses, TimeSpan deadline, Stopwatch clock)
{
    var last = "unknown";
    while (clock.Elapsed < deadline)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders/{orderId}");
        request.Headers.Add("X-Dev-User", $"merchant:{merchant}");
        try
        {
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                last = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString() ?? last;
                if (finalStatuses.Contains(last, StringComparer.Ordinal))
                {
                    return last;
                }
            }
        }
        catch (HttpRequestException)
        {
            // Orders is busy or restarting; ask again.
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    return last;
}
