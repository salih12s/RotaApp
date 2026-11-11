using System.Diagnostics.CodeAnalysis;

namespace Rota.BuildingBlocks.Primitives;

/// <summary>
/// An expected business failure. <see cref="Code"/> is the error catalog id
/// (docs/06-security-and-errors.md §5), e.g. <c>invalid-order-transition</c>.
/// </summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "'Error' is a VB keyword only; Rota is C#-only and the name is fixed by docs/08-roadmap.md F01.")]
public sealed record Error(string Code, string Message);
