using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.IntegrationTests;

/// <summary>
/// The real Orders API in memory (no port), wired to the test container and test keys.
/// Environment stays Development: DevAuth refuses to start anywhere else (ADR-006).
/// </summary>
public sealed class OrdersApiFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    IReadOnlyDictionary<string, string>? extraSettings = null)
    : WebApplicationFactory<Program>
{
    /// <summary>
    /// Fixed start, and every read moves 1 ms forward: created_at values differ and stay ordered
    /// without the test ever reading the real clock.
    /// </summary>
    public FakeTimeProvider Time { get; } = new(TestClock.Start) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in OrdersTestSettings.Values(connectionString))
        {
            builder.UseSetting(key, value);
        }

        // Applied after the defaults, so a messaging test can switch Messaging:Enabled back on.
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            configureServices?.Invoke(services);
        });
    }

    public HttpClient CreateClientAs(string devUser) => CreateClient().As(devUser);
}
