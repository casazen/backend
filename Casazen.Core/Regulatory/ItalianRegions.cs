namespace Casazen.Core.Regulatory;

/// <summary>One of the 20 Italian regions: the ISTAT code of the list, CasaZen's own code and the SEO slug.</summary>
/// <param name="IstatCode">ISTAT code of the region, two digits (the "Codice Regione" of the official comuni list).</param>
/// <param name="Code">
/// CasaZen's three-letter code (<c>LOM</c>): the one of <c>Compliance:RequiredDocuments</c>, of the tourist tax rates and of the
/// SEO pages. Not an official code: it only exists because those features were built on it before the ISTAT list.
/// </param>
/// <param name="Name">Name of the region (the list writes the bilingual regions with both names; the import checks the start).</param>
/// <param name="Slug">Slug of the region in the public SEO URLs.</param>
public sealed record ItalianRegion(string IstatCode, string Code, string Name, string Slug);

/// <summary>
/// The 20 regions of the Italian Republic (Costituzione, art. 131) with their ISTAT numbering 01–20 (the "Codice Regione" of
/// the official list "Elenco dei comuni italiani"). A closed classification: the import rejects a row whose region code is
/// not here or whose region name does not match its code, and the region of a comune is always derived from this table.
/// </summary>
public static class ItalianRegions
{
    public static IReadOnlyList<ItalianRegion> All { get; } =
    [
        new("01", "PIE", "Piemonte", "piemonte"),
        new("02", "VDA", "Valle d'Aosta", "valle-d-aosta"),
        new("03", "LOM", "Lombardia", "lombardia"),
        new("04", "TAA", "Trentino-Alto Adige", "trentino-alto-adige"),
        new("05", "VEN", "Veneto", "veneto"),
        new("06", "FVG", "Friuli-Venezia Giulia", "friuli-venezia-giulia"),
        new("07", "LIG", "Liguria", "liguria"),
        new("08", "EMR", "Emilia-Romagna", "emilia-romagna"),
        new("09", "TOS", "Toscana", "toscana"),
        new("10", "UMB", "Umbria", "umbria"),
        new("11", "MAR", "Marche", "marche"),
        new("12", "LAZ", "Lazio", "lazio"),
        new("13", "ABR", "Abruzzo", "abruzzo"),
        new("14", "MOL", "Molise", "molise"),
        new("15", "CAM", "Campania", "campania"),
        new("16", "PUG", "Puglia", "puglia"),
        new("17", "BAS", "Basilicata", "basilicata"),
        new("18", "CAL", "Calabria", "calabria"),
        new("19", "SIC", "Sicilia", "sicilia"),
        new("20", "SAR", "Sardegna", "sardegna"),
    ];

    public static ItalianRegion? FindByIstatCode(string? istatCode) =>
        istatCode is null ? null : All.FirstOrDefault(r => string.Equals(r.IstatCode, istatCode, StringComparison.Ordinal));

    public static ItalianRegion? FindByCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : All.FirstOrDefault(r => string.Equals(r.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    public static ItalianRegion? FindBySlug(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? null : All.FirstOrDefault(r => string.Equals(r.Slug, slug.Trim(), StringComparison.OrdinalIgnoreCase));

    public static ItalianRegion? FindByName(string? name)
    {
        var normalized = ComuneNames.Normalize(name);
        if (normalized.Length == 0)
            return null;

        return All.FirstOrDefault(r =>
        {
            var expected = ComuneNames.Normalize(r.Name);
            return normalized == expected || normalized.StartsWith(expected + " ", StringComparison.Ordinal);
        });
    }
}
