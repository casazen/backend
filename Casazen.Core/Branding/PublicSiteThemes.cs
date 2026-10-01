namespace Casazen.Core.Branding;

/// <summary>
/// Visual themes of the public booking site (<c>Org.PublicThemeId</c>, spec US-023 AC2). Only the themes the public
/// frontend supports (<c>src/styles/public-tokens.css</c>, mirrored in <c>src/lib/public-site-themes.ts</c>): keep the
/// three lists in sync. A theme added here without its frontend tokens would be accepted but render as the default.
/// </summary>
public static class PublicSiteThemes
{
    public const string Mare = "mare";
    public const string Montagna = "montagna";
    public const string Urban = "urban";

    /// <summary>Theme of an org that never chose one, and of any stored value the frontend no longer supports.</summary>
    public const string Default = Mare;

    /// <summary>Supported theme ids, in the order the console offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [Mare, Montagna, Urban];

    /// <summary>True when <paramref name="themeId"/> is a supported theme id (exact, lower case).</summary>
    public static bool IsSupported(string? themeId) => themeId is not null && All.Contains(themeId, StringComparer.Ordinal);

    /// <summary>
    /// The theme the public site renders for a stored value: the value itself when supported, otherwise
    /// <see cref="Default"/> (unset, or a legacy value written before the themes were validated).
    /// </summary>
    public static string Resolve(string? storedThemeId)
    {
        var normalized = storedThemeId?.Trim().ToLowerInvariant();
        return IsSupported(normalized) ? normalized! : Default;
    }
}
