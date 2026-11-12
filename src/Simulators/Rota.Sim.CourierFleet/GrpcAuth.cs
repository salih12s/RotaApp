using Grpc.Core;
using Rota.Sim.Common;

namespace Rota.Sim.CourierFleet;

/// <summary>gRPC call metadata for an identity: <c>authorization: Bearer ...</c>, or the dev header when not using Keycloak.</summary>
internal static class GrpcAuth
{
    /// <summary>Built per call (stream), not once: a token lives minutes, a stream reconnects for hours.</summary>
    public static async Task<Metadata> MetadataAsync(this SimCredentials credentials, string devUser)
    {
        var token = await credentials.BearerAsync(devUser);
        return token is null
            ? new Metadata { { "x-dev-user", devUser } }
            : new Metadata { { "authorization", $"Bearer {token}" } };
    }
}
