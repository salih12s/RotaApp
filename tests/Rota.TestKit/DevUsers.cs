using System.Globalization;

namespace Rota.TestKit;

/// <summary>
/// Values for the <c>X-Dev-User</c> header (DevAuth, ADR-006). Each test makes its own ids,
/// e.g. <c>DevUsers.NewMerchantId()</c>, so tests never see each other's data.
/// </summary>
public static class DevUsers
{
    public const string HeaderName = "X-Dev-User";

    public static string NewMerchantId() => $"merchant-{Guid.CreateVersion7():N}";

    public static string Merchant(string merchantId) => $"merchant:{merchantId}";

    public static string Courier(Guid courierId) => $"courier:{courierId.ToString("D", CultureInfo.InvariantCulture)}";

    public static string Dispatcher(string dispatcherId) => $"dispatcher:{dispatcherId}";

    /// <summary>Sends every request of this client as the given user.</summary>
    public static HttpClient As(this HttpClient client, string devUser)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Remove(HeaderName);
        client.DefaultRequestHeaders.Add(HeaderName, devUser);
        return client;
    }
}
