using System.Security.Cryptography;

namespace Rota.TestKit.Orders;

/// <summary>
/// Test-only configuration for the Orders API. The hash key is the one <see cref="OrderTestData.Hasher"/>
/// uses, so a test can check a stored hash against a returned code.
/// </summary>
public static class OrdersTestSettings
{
    private static readonly Lazy<string> _signingKey = new(() =>
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    });

    public static byte[] ResponseEncryptionKey { get; } = RandomNumberGenerator.GetBytes(32);

    public static string TrackingSigningKeyPem => _signingKey.Value;

    public static IReadOnlyDictionary<string, string> Values(string connectionString) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Orders"] = connectionString,
            // Tests that do not exercise messaging run without a broker; messaging tests switch it on.
            ["Messaging:Enabled"] = "false",
            ["Orders:DeliveryCodeHashKey"] = Convert.ToBase64String(OrderTestData.HashKey),
            ["Orders:ResponseEncryptionKey"] = Convert.ToBase64String(ResponseEncryptionKey),
            ["Orders:TrackingSigningKey"] = TrackingSigningKeyPem,
        };
}
