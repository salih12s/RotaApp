using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Syntax.Elements.Types;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Rota.TestKit.Architecture;

/// <summary>Architecture rule shared by every service: Domain/ is pure (CLAUDE.md "Servis içi yapı").</summary>
public static class DomainRules
{
    /// <summary>
    /// Web and persistence frameworks, plus the infrastructure building blocks that wrap them.
    /// Rota.BuildingBlocks.Primitives (Result/Error) is allowed.
    /// </summary>
    public const string ForbiddenNamespaces =
        @"^(Microsoft\.EntityFrameworkCore|Microsoft\.AspNetCore|Rota\.BuildingBlocks\.(Web|Persistence|Messaging))(\..*)?$";

    public static IArchRule MustNotDependOnFrameworks(GivenTypesConjunction subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        return subjects.Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ForbiddenNamespaces);
    }

    public static IArchRule DomainMustBePure(string domainNamespace) =>
        MustNotDependOnFrameworks(Types().That().ResideInNamespaceOrBelow(domainNamespace));
}
