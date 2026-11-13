using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Rota.BuildingBlocks.Web.Auth;

/// <summary>The <c>Auth:Service</c> section: the Keycloak client this service uses when it calls another Rota service (docs/06 §1).</summary>
public sealed class ServiceCredentialsOptions
{
    public const string SectionName = "Auth:Service";

    public string? ClientId { get; set; }

    /// <summary>A secret: user-secrets, an environment variable or the platform's secret store, never the repository.</summary>
    public string? ClientSecret { get; set; }
}

/// <summary>
/// How a service proves who it is to another service: a client-credentials token from Keycloak (role <c>service</c>), renewed shortly
/// before it expires. Without a configured client, Development falls back to the <c>X-Dev-User</c> header (ADR-006: the caller adds it
/// when <see cref="GetBearerAsync"/> returns null); anywhere else a missing client is an error, not a silent downgrade.
/// </summary>
public sealed class ServiceCredentials(
    IHttpClientFactory httpFactory,
    IOptions<RotaAuthOptions> auth,
    IOptions<ServiceCredentialsOptions> service,
    IHostEnvironment environment,
    TimeProvider time) : IDisposable
{
    private static readonly TimeSpan _renewBefore = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _renewAt;

    /// <summary>The bearer token to send, or null when the dev header should be sent instead.</summary>
    public async Task<string?> GetBearerAsync(CancellationToken cancellationToken)
    {
        var client = service.Value;
        if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret))
        {
            return environment.IsDevelopment()
                ? null
                : throw new InvalidOperationException($"'{ServiceCredentialsOptions.SectionName}:ClientId' and ':ClientSecret' are required outside Development.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is null || time.GetUtcNow() >= _renewAt)
            {
                var authority = auth.Value.Authority ?? throw new InvalidOperationException("'Auth:Authority' is required to fetch a service token.");
                using var http = httpFactory.CreateClient(nameof(ServiceCredentials));
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = client.ClientId,
                    ["client_secret"] = client.ClientSecret,
                });
                using var response = await http.PostAsync(new Uri($"{authority.TrimEnd('/')}/protocol/openid-connect/token"), form, cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                _token = json.GetProperty("access_token").GetString()!;
                _renewAt = time.GetUtcNow() + TimeSpan.FromSeconds(json.GetProperty("expires_in").GetInt32()) - _renewBefore;
            }

            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}

public static class ServiceCredentialsExtensions
{
    /// <summary>Registers <see cref="ServiceCredentials"/> for a service that calls other Rota services.</summary>
    public static IHostApplicationBuilder AddRotaServiceCredentials(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient();
        builder.Services.AddOptions<RotaAuthOptions>().BindConfiguration(RotaAuthOptions.SectionName);
        builder.Services.AddOptions<ServiceCredentialsOptions>().BindConfiguration(ServiceCredentialsOptions.SectionName);
        builder.Services.AddSingleton<ServiceCredentials>();
        return builder;
    }
}
