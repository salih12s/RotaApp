using System.Net;
using System.Net.Http.Json;
using Rota.Orders.Api.Features.CreateOrder;
using Rota.Orders.Api.Features.ListOrders;
using Rota.Orders.IntegrationTests.Support;
using Rota.TestKit;

namespace Rota.Orders.IntegrationTests.Features;

/// <summary><c>GET /orders</c>: keyset pagination (PASS 5), ownership, filters, input errors.</summary>
public sealed class ListOrdersTests(OrdersDatabaseFixture fixture)
{
    [Fact]
    public async Task List_WhenOrdersAreCreatedWhilePaging_ReturnsEachOldOrderExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));
        var existing = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            existing.Add(await CreateAsync(client, ct));
        }

        var seen = new List<Guid>();
        var page = await ListAsync(client, "?limit=3", ct);
        seen.AddRange(page.Items.Select(o => o.Id));

        // New orders arrive between page requests. With OFFSET they would push rows onto the
        // next page (duplicates); with a (created_at, id) cursor they land before page 1.
        for (var i = 0; i < 2; i++)
        {
            await CreateAsync(client, ct);
        }

        while (page.NextCursor is not null)
        {
            page = await ListAsync(client, $"?limit=3&after={page.NextCursor}", ct);
            seen.AddRange(page.Items.Select(o => o.Id));
        }

        seen.ShouldBeUnique();
        seen.ShouldBe(Enumerable.Reverse(existing), "newest first, every old order once");
    }

    [Fact]
    public async Task List_AsMerchant_ReturnsOnlyOwnOrdersAndAppliesStatusFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        using var mine = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));
        using var other = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));
        var own = await CreateAsync(mine, ct);
        var cancelled = await CreateAsync(mine, ct);
        await CreateAsync(other, ct);
        using (var cancel = await mine.PostAsync(new Uri($"/api/v1/orders/{cancelled}/cancel", UriKind.Relative), null, ct))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var all = await ListAsync(mine, string.Empty, ct);
        var onlyCreated = await ListAsync(mine, "?status=created", ct);

        all.Items.Select(o => o.Id).ShouldBe([cancelled, own]);
        all.NextCursor.ShouldBeNull();
        onlyCreated.Items.ShouldHaveSingleItem().Id.ShouldBe(own);
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=101", "limit")]
    [InlineData("?status=Teslim", "status")]
    [InlineData("?after=bm90LWEtY3Vyc29y", "after")]   // base64url of "not-a-cursor"
    [InlineData("?after=%25%25%25", "after")]          // not base64url at all
    public async Task List_WithInvalidQuery_Returns400NamingTheParameter(string query, string parameter)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Api.CreateClientAs(DevUsers.Merchant(DevUsers.NewMerchantId()));

        using var response = await client.GetAsync(new Uri($"/api/v1/orders{query}", UriKind.Relative), ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation-failed", ct);
        problem.GetProperty("errors").TryGetProperty(parameter, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task List_AsCourier_Returns403()
    {
        var ct = TestContext.Current.CancellationToken;
        using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(Guid.CreateVersion7()));

        using var response = await courier.GetAsync(new Uri("/api/v1/orders", UriKind.Relative), ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden", ct);
    }

    private static async Task<Guid> CreateAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.PostOrderAsync(OrderRequests.Valid(), OrderRequests.NewKey(), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreateOrderResponse>(ct))!.Id;
    }

    private static async Task<OrderPage> ListAsync(HttpClient client, string query, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/orders{query}", UriKind.Relative), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<OrderPage>(ct)).ShouldNotBeNull();
    }
}
