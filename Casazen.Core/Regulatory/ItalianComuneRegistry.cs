namespace Casazen.Core.Regulatory;

public sealed record ComuneInfo(
    string Code,
    string Name,
    string RegionCode,
    string RegionSlug,
    string ComuneSlug);

public static class ItalianComuneRegistry
{
    private static readonly ComuneInfo[] Comuni =
    [
        new("013075", "Como", "LOM", "lombardia", "como"),
        new("013040", "Bellagio", "LOM", "lombardia", "bellagio"),
        new("013133", "Menaggio", "LOM", "lombardia", "menaggio"),
        new("013182", "Varenna", "LOM", "lombardia", "varenna"),
        new("015146", "Milano", "LOM", "lombardia", "milano"),
        new("058091", "Roma", "LAZ", "lazio", "roma"),
        new("048017", "Firenze", "TOS", "toscana", "firenze"),
        new("010025", "Torino", "PIE", "piemonte", "torino"),
        new("063049", "Napoli", "CAM", "campania", "napoli"),
        new("027042", "Venezia", "VEN", "veneto", "venezia"),
        new("037006", "Bologna", "EMR", "emilia-romagna", "bologna"),
        new("082053", "Palermo", "SIC", "sicilia", "palermo"),
    ];

    public static IReadOnlyList<ComuneInfo> All => Comuni;

    public static IReadOnlyList<string> AllCodes => Comuni.Select(c => c.Code).ToArray();

    public static ComuneInfo? GetByCode(string comuneCode) =>
        Comuni.FirstOrDefault(c => c.Code.Equals(comuneCode, StringComparison.OrdinalIgnoreCase));

    public static ComuneInfo? GetBySlug(string comuneSlug) =>
        Comuni.FirstOrDefault(c => c.ComuneSlug.Equals(comuneSlug, StringComparison.OrdinalIgnoreCase));

    public static ComuneInfo? GetByRegionAndComuneSlug(string regionSlug, string comuneSlug) =>
        Comuni.FirstOrDefault(c =>
            c.RegionSlug.Equals(regionSlug, StringComparison.OrdinalIgnoreCase) &&
            c.ComuneSlug.Equals(comuneSlug, StringComparison.OrdinalIgnoreCase));
}
