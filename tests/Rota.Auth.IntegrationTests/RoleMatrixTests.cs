extern alias OrdersApi;

using System.Net;

namespace Rota.Auth.IntegrationTests;

/// <summary>
/// R-10: every protected endpoint of every service against every role, with real Keycloak tokens and the services running as in
/// production. No token is 401; a token whose role is not allowed is 403; an allowed role gets past authentication and authorization.
/// (Ownership, "someone else's resource is 404", is in <see cref="TokenValidationTests"/>.)
/// </summary>
[Trait("Category", "Auth")]
public sealed class RoleMatrixTests(AuthFixture fixture)
{
    private const string Id = "00000000-0000-4000-8000-0000000000aa";

    private static readonly string[] Roles = ["none", "merchant", "courier", "dispatcher", "service"];

    // service, method, path, roles that are allowed (docs/06 §2)
    private static readonly (string Service, string Method, string Path, string[] Allowed)[] Endpoints =
    [
        ("orders", "POST", "/api/v1/orders", ["merchant"]),
        ("orders", "GET", $"/api/v1/orders/{Id}", ["merchant", "courier", "dispatcher"]),
        ("orders", "GET", "/api/v1/orders", ["merchant", "dispatcher"]),
        ("orders", "POST", $"/api/v1/orders/{Id}/cancel", ["merchant", "dispatcher"]),
        ("orders", "POST", $"/api/v1/orders/{Id}/pickup", ["courier"]),
        ("orders", "POST", $"/api/v1/orders/{Id}/deliver", ["courier"]),
        ("orders", "POST", $"/api/v1/orders/{Id}/delivery-failed", ["courier"]),
        ("orders", "POST", $"/api/v1/orders/{Id}/returned", ["courier", "dispatcher"]),
        ("orders", "POST", $"/api/v1/orders/{Id}/delivery-code/unlock", ["dispatcher"]),
        ("dispatch", "POST", "/api/v1/couriers/me/shift/start", ["courier"]),
        ("dispatch", "POST", "/api/v1/couriers/me/shift/end", ["courier"]),
        ("dispatch", "GET", "/api/v1/couriers/me/offers", ["courier"]),
        ("dispatch", "POST", $"/api/v1/offers/{Id}/accept", ["courier"]),
        ("dispatch", "POST", $"/api/v1/offers/{Id}/reject", ["courier"]),
        ("dispatch", "GET", "/api/v1/couriers", ["dispatcher"]),
        ("dispatch", "POST", $"/api/v1/dispatch-jobs/{Id}/manual-assign", ["dispatcher"]),
        ("dispatch", "POST", $"/api/v1/dispatch-jobs/{Id}/restart", ["dispatcher"]),
        ("notifications", "PUT", "/api/v1/webhook-subscription", ["merchant"]),
        ("notifications", "POST", "/api/v1/webhook-subscription/rotate-secret", ["merchant"]),
        ("notifications", "GET", "/api/v1/webhook-deliveries", ["merchant"]),
        ("notifications", "POST", $"/api/v1/webhook-deliveries/{Id}/replay", ["merchant"]),
    ];

    /// <summary>
    /// Denied combinations for every service. "Allowed" combinations only for Orders: that is the service with a migrated database here;
    /// the others would answer 500 for want of tables, which says nothing about authorization.
    /// </summary>
    public static TheoryData<string, string, string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, string, string, bool>();
        foreach (var (service, method, path, allowed) in Endpoints)
        {
            foreach (var role in Roles)
            {
                var isAllowed = allowed.Contains(role, StringComparer.Ordinal);
                if (isAllowed && service != "orders")
                {
                    continue;
                }

                data.Add(service, method, path, role, isAllowed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Role_matrix(string service, string method, string path, string role, bool allowed)
    {
        var token = await fixture.Tokens.ForRoleAsync(role);
        using var client = service switch
        {
            "orders" => fixture.Orders.CreateClient(),
            "dispatch" => fixture.Dispatch.CreateClient(),
            _ => fixture.Notifications.CreateClient(),
        };

        using var request = Bearer.Request(new HttpMethod(method), path, token);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        if (role == "none")
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{method} {path} without a token");
        }
        else if (allowed)
        {
            response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, $"{method} {path} as {role}");
            response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, $"{method} {path} as {role}");
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path} as {role}");
        }
    }
}
