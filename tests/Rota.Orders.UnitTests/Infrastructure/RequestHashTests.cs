using System.Text.Json;
using Rota.Orders.Api.Features.CreateOrder;

namespace Rota.Orders.UnitTests.Infrastructure;

public sealed class RequestHashTests
{
    private static readonly CreateOrderRequest _request = new(
        "PZ-1",
        new LocationRequest(40.9903, 29.0290, "Kadıköy"),
        new LocationRequest(40.9850, 29.0400, "Moda"),
        new CustomerRequest("Ayşe Y.", "+905551112233"));

    [Fact]
    public void Compute_SameRequestSentWithDifferentFormatting_GivesSameHash()
    {
        // Key order, spacing and trailing zeros differ; the parsed request is the same.
        const string reordered = """
            { "customer": { "phone": "+905551112233", "name": "Ayşe Y." },
              "dropoff": { "address": "Moda", "lon": 29.04, "lat": 40.985 },
              "pickup":  { "lat": 40.99030, "lon": 29.0290, "address": "Kadıköy" },
              "merchantOrderRef": "PZ-1" }
            """;
        var parsed = JsonSerializer.Deserialize<CreateOrderRequest>(reordered, JsonSerializerOptions.Web)!;

        RequestHash.Compute(parsed).ShouldBe(RequestHash.Compute(_request));
    }

    [Fact]
    public void Compute_AnyChangedValue_GivesDifferentHash()
    {
        var changed = _request with { Dropoff = _request.Dropoff with { Lat = 40.9851 } };

        RequestHash.Compute(changed).ShouldNotBe(RequestHash.Compute(_request));
    }
}
