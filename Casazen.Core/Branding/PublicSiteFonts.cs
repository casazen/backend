namespace Casazen.Core.Branding;

/// <summary>
/// Font pairs of the public booking site (<c>Org.PublicFontId</c>, DB-01): Editoriale (serif headings with warmth), Moderno
/// (all sans-serif) and Classico (high-contrast magazine serif). The host picks one apart from the theme; an org that never
/// chose keeps the font its theme starts with (Terra and Mare: Editoriale, Città: Moderno), which is what a <c>null</c>
/// id means. Keep in sync with <c>src/lib/public-site-themes.ts</c> and <c>src/styles/public-tokens.css</c>.
/// </summary>
public static class PublicSiteFonts
{
    public const string Editoriale = "editoriale";
    public const string Moderno = "moderno";
    public const string Classico = "classico";

    /// <summary>Supported font ids, in the order the console offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [Editoriale, Moderno, Classico];

    /// <summary>True when <paramref name="fontId"/> is a supported font id (exact, lower case).</summary>
    public static bool IsSupported(string? fontId) => fontId is not null && All.Contains(fontId, StringComparer.Ordinal);

    /// <summary>
    /// The font the public site renders for a stored value: the value itself when supported, otherwise <c>null</c>, which is
    /// "the font of the theme" (unset, or a value this release does not know).
    /// </summary>
    public static string? Resolve(string? storedFontId)
    {
        var normalized = storedFontId?.Trim().ToLowerInvariant();
        return IsSupported(normalized) ? normalized : null;
    }
}
