using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// A comune as the SEO pages and the signup attribution use it (SU-04): its ISTAT code, name, CasaZen region code and the
/// slugs of the public URLs. Always built from the official list (<see cref="From"/>), never written by hand.
/// </summary>
public sealed record ComuneInfo(
    string Code,
    string Name,
    string RegionCode,
    string RegionSlug,
    string ComuneSlug)
{
    /// <summary>The SEO view of a comune of the official list; slugs come from its name and its region.</summary>
    public static ComuneInfo From(Comune comune)
    {
        ArgumentNullException.ThrowIfNull(comune);
        var region = ItalianRegions.FindByIstatCode(comune.RegionIstatCode);
        return new ComuneInfo(
            comune.IstatCode,
            comune.Name,
            region?.Code ?? comune.RegionIstatCode,
            region?.Slug ?? ComuneNames.Slugify(comune.RegionName),
            ComuneNames.Slugify(comune.Name));
    }
}

/// <summary>
/// The comuni the public SEO pages are generated for (the pilot of the SEO content, A8-24): a product choice, so it is the only
/// thing written here, as name and province plate code. The ISTAT code, the region and the slugs come from the official list
/// (<c>ISeoComuneCatalog</c>): a pilot that is not found in it is skipped, never given an invented code.
/// </summary>
public static class SeoPilotComuni
{
    public static IReadOnlyList<(string Name, string ProvinceCode)> All { get; } =
    [
        ("Como", "CO"),
        ("Bellagio", "CO"),
        ("Menaggio", "CO"),
        ("Varenna", "LC"),
        ("Milano", "MI"),
        ("Roma", "RM"),
        ("Firenze", "FI"),
        ("Torino", "TO"),
        ("Napoli", "NA"),
        ("Venezia", "VE"),
        ("Bologna", "BO"),
        ("Palermo", "PA"),
    ];
}
