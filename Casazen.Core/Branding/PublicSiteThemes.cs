namespace Casazen.Core.Branding;

/// <summary>
/// Visual themes of the public booking site (<c>Org.PublicThemeId</c>, spec US-023 AC2, DB-01): Terra (terracotta on
/// cream), Mare (petrol blue on white) and Città (ink on stone). Only the themes the public frontend supports
/// (<c>src/styles/public-tokens.css</c>, mirrored in <c>src/lib/public-site-themes.ts</c>): keep the three lists in sync. A
/// theme added here without its frontend tokens would be accepted but render as the default.
/// </summary>
/// <remarks>
/// Before DB-01 the ids were <c>mare</c> (terracotta on cream), <c>montagna</c> (green) and <c>urban</c> (indigo). The id
/// <c>mare</c> now belongs to a different look (petrol blue), so the migration <c>AddPublicSiteFontAndThemeIds</c>
/// rewrites the stored values (<c>mare</c> and <c>montagna</c> to <c>terra</c>, <c>urban</c> to <c>citta</c>); the two ids
/// that vanished are still understood when read (<see cref="Resolve"/>) for the window between the migration and the
/// deploy of the frontend, and for two releases after it. They are not accepted when written (<see cref="IsSupported"/>).
/// </remarks>
public static class PublicSiteThemes
{
    public const string Terra = "terra";
    public const string Mare = "mare";
    public const string Citta = "citta";

    /// <summary>Theme of an org that never chose one, and of any stored value the frontend does not support.</summary>
    public const string Default = Terra;

    /// <summary>Supported theme ids, in the order the console offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [Terra, Mare, Citta];

    /// <summary>
    /// Ids that existed before DB-01 and no longer name a theme, with the theme that replaces them. <c>mare</c> is not here: it
    /// is a supported id again, with a new look, and the migration is what moves the orgs that had the old one.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyIds = new(StringComparer.Ordinal)
    {
        ["montagna"] = Terra,
        ["urban"] = Citta,
    };

    /// <summary>True when <paramref name="themeId"/> is a supported theme id (exact, lower case).</summary>
    public static bool IsSupported(string? themeId) => themeId is not null && All.Contains(themeId, StringComparer.Ordinal);

    /// <summary>
    /// The theme the public site renders for a stored value: the value itself when supported, the replacement of an id from
    /// before DB-01 (<c>montagna</c>, <c>urban</c>), otherwise <see cref="Default"/> (unset, or an unknown legacy value).
    /// </summary>
    public static string Resolve(string? storedThemeId)
    {
        var normalized = storedThemeId?.Trim().ToLowerInvariant();
        if (IsSupported(normalized))
            return normalized!;

        return normalized is not null && LegacyIds.TryGetValue(normalized, out var replacement) ? replacement : Default;
    }
}
