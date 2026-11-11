using System.Net.Http.Json;
using Rota.Orders.Api.Features.CreateOrder;

namespace Rota.Orders.IntegrationTests.Support;

/// <summary>Fixtures for order requests: a valid Kadıköy → Moda order, varied per test.</summary>
public static class OrderRequests
{
    public static CreateOrderRequest Valid(string? merchantOrderRef = null) => new(
        merchantOrderRef ?? $"PZ-{Guid.CreateVersion7():N}",
        new LocationRequest(40.9903, 29.0290, "Kadıköy Şube, Moda Cd. 10"),
        new LocationRequest(40.9850, 29.0400, "Caferağa Mh. Moda"),
        new CustomerRequest("Ayşe Y.", "+905551112233"));

    public static string NewKey() => $"key-{Guid.CreateVersion7():N}";

    public static async Task<HttpResponseMessage> PostOrderAsync(
        this HttpClient client, CreateOrderRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(request) };
        if (idempotencyKey is not null)
        {
            message.Headers.Add(CreateOrderEndpoint.IdempotencyKeyHeader, idempotencyKey);
        }

        return await client.SendAsync(message, cancellationToken);
    }
}
