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
        new("015146", "Milano", "LOM", "lombardia", "milano"),
        new("058091", "Roma", "LAZ", "lazio", "roma"),
        new("048017", "Firenze", "TOS", "toscana", "firenze"),
    ];

    public static ComuneInfo? GetByCode(string comuneCode) =>
        Comuni.FirstOrDefault(c => c.Code.Equals(comuneCode, StringComparison.OrdinalIgnoreCase));

    public static ComuneInfo? GetBySlug(string comuneSlug) =>
        Comuni.FirstOrDefault(c => c.ComuneSlug.Equals(comuneSlug, StringComparison.OrdinalIgnoreCase));

    public static ComuneInfo? GetByRegionAndComuneSlug(string regionSlug, string comuneSlug) =>
        Comuni.FirstOrDefault(c =>
            c.RegionSlug.Equals(regionSlug, StringComparison.OrdinalIgnoreCase) &&
            c.ComuneSlug.Equals(comuneSlug, StringComparison.OrdinalIgnoreCase));
}
