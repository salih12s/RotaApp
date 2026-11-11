using Microsoft.AspNetCore.Http;

namespace Rota.Orders.UnitTests.ArchitectureSamples;

// Deliberately "wrong" domain-like code: the negative controls in ArchitectureTests must flag these.

internal static class SignatureSample
{
    public static IResult Make() => Results.Ok();
}

internal static class BodySample
{
    public static object Make() => Results.Ok();
}

internal static class LambdaSample
{
    public static Func<object> Make() => () => Results.Ok();
}
