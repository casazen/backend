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

    public static ComuneInfo? GetByName(string name) =>
        Comuni.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Legacy cadastral / invite codes still stored in supplier profiles (e.g. H501 = Roma).
    /// </summary>
    private static readonly Dictionary<string, string> LegacyCadastralCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["H501"] = "Roma",
            ["F205"] = "Firenze",
        };

    /// <summary>
    /// Returns all equivalent identifiers (name, ISTAT code, slug, legacy codes) for matching.
    /// </summary>
    public static IReadOnlySet<string> BuildEquivalenceSet(string cityOrCode)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cityOrCode))
            return set;

        var trimmed = cityOrCode.Trim();
        set.Add(trimmed);

        if (trimmed.Equals("Rome", StringComparison.OrdinalIgnoreCase))
            set.Add("Roma");

        var info = GetByCode(trimmed) ?? GetByName(trimmed) ?? GetBySlug(trimmed);

        if (info is null && LegacyCadastralCodes.TryGetValue(trimmed, out var legacyName))
            info = GetByName(legacyName);

        if (info is not null)
        {
            set.Add(info.Code);
            set.Add(info.Name);
            set.Add(info.ComuneSlug);

            foreach (var (legacyCode, name) in LegacyCadastralCodes)
            {
                if (name.Equals(info.Name, StringComparison.OrdinalIgnoreCase))
                    set.Add(legacyCode);
            }
        }

        return set;
    }

    /// <summary>
    /// True when two city/code values refer to the same comune (e.g. "Roma" ↔ "H501" ↔ "058091").
    /// </summary>
    public static bool Matches(string cityOrCodeA, string cityOrCodeB)
    {
        if (string.IsNullOrWhiteSpace(cityOrCodeA) || string.IsNullOrWhiteSpace(cityOrCodeB))
            return false;

        var setA = BuildEquivalenceSet(cityOrCodeA);
        foreach (var alias in BuildEquivalenceSet(cityOrCodeB))
        {
            if (setA.Contains(alias))
                return true;
        }

        return false;
    }
}
