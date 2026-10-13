using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// The comuni of the SEO pages and of the signup attribution, from the official ISTAT list (SU-04). Any comune of the list
/// is known by its code; the slug lookups of the public URLs cover only the pilot comuni (<see cref="SeoPilotComuni"/>),
/// because a name is not unique in Italy. Empty until the list is imported.
/// </summary>
public interface ISeoComuneCatalog
{
    /// <summary>The comune with this ISTAT code (active), or <c>null</c> when it is not in the list.</summary>
    Task<ComuneInfo?> GetByCodeAsync(string? istatCode, CancellationToken cancellationToken = default);

    /// <summary>The comuni of these ISTAT codes (active), by code; a code that is not in the list is absent.</summary>
    Task<IReadOnlyDictionary<string, ComuneInfo>> GetByCodesAsync(
        IEnumerable<string> istatCodes,
        CancellationToken cancellationToken = default);

    /// <summary>The pilot comune with this slug (<c>como</c>), or <c>null</c>.</summary>
    Task<ComuneInfo?> GetPilotBySlugAsync(string? comuneSlug, CancellationToken cancellationToken = default);

    /// <summary>The pilot comune with these region and comune slugs, or <c>null</c>.</summary>
    Task<ComuneInfo?> GetPilotByRegionAndComuneSlugAsync(
        string? regionSlug,
        string? comuneSlug,
        CancellationToken cancellationToken = default);

    /// <summary>The pilot comuni found in the list (a pilot that is not in it is left out).</summary>
    Task<IReadOnlyList<ComuneInfo>> GetPilotsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The comune of a slug (pilot) or of an ISTAT code, <c>null</c> when CasaZen does not know it: the way the signup
    /// attribution resolves the <c>?comune=</c> of a landing page.
    /// </summary>
    Task<ComuneInfo?> ResolveSlugOrCodeAsync(string? value, CancellationToken cancellationToken = default);
}
