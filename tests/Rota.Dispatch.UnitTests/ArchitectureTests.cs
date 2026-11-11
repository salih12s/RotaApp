using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Rota.Dispatch.Api.Domain;
using Rota.TestKit.Architecture;
using Rota.Dispatch.UnitTests.ArchitectureSamples;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Rota.Dispatch.UnitTests;

/// <summary>PASS criterion 5: Domain/ must not depend on EF Core, ASP.NET Core or infrastructure building blocks.</summary>
public sealed class ArchitectureTests
{
    private const string DomainNamespace = "Rota.Dispatch.Api.Domain";

    private static readonly ArchUnitNET.Domain.Architecture _architecture =
        new ArchLoader().LoadAssemblies(typeof(Courier).Assembly, typeof(ArchitectureTests).Assembly).Build();

    [Fact]
    public void Domain_DoesNotDependOnFrameworks() =>
        DomainRules.DomainMustBePure(DomainNamespace).Check(_architecture);

    [Fact]
    public void Domain_Namespace_IsNotEmpty() =>
        // Guards against a vacuous pass: a renamed namespace would make the rule check zero types.
        _architecture.Types.Count(t => t.Namespace.FullName == DomainNamespace).ShouldBeGreaterThan(3);

    [Theory]
    [InlineData(nameof(SignatureSample))]
    [InlineData(nameof(BodySample))]
    [InlineData(nameof(LambdaSample))]
    public void Rule_OnImpureSample_FindsViolation(string sampleName) =>
        // Negative control: proves the rule can fail. A rule that never fails protects nothing.
        DomainRules.MustNotDependOnFrameworks(
                Types().That().HaveFullName($"{typeof(SignatureSample).Namespace}.{sampleName}"))
            .HasNoViolations(_architecture).ShouldBeFalse();
}
