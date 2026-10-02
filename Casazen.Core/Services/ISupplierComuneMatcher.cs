using Casazen.Core.Entities;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// Decides which supplier profiles operate in a comune (SU-04, A4-12). A supplier covers the comuni it chose from the
/// official list (<see cref="SupplierProfile.ComuneIstatCodesJson"/>) and those it wrote as free text before
/// (<see cref="SupplierProfile.ComuniJson"/>: an ISTAT code, a cadastral code such as <c>H501</c>, a name), the latter resolved
/// against the list. The match is by ISTAT code; only when the comune or a written entry cannot be resolved (list not imported,
/// an unknown or ambiguous name) does it fall back to the written name, as before.
/// </summary>
public interface ISupplierComuneMatcher
{
    /// <summary>The profiles of <paramref name="suppliers"/> that operate in <paramref name="target"/>, in the same order.</summary>
    Task<IReadOnlyList<SupplierProfile>> FilterAsync(
        IReadOnlyCollection<SupplierProfile> suppliers,
        ComuneTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="supplier"/> operates in <paramref name="target"/>.</summary>
    Task<bool> CoversAsync(SupplierProfile supplier, ComuneTarget target, CancellationToken cancellationToken = default);
}
