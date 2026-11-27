namespace Casazen.Core.SiteProfile;

public static class ListingLanguages
{
    public const string Italian = "it";

    public const string English = "en";

    public static IReadOnlyList<string> All { get; } =
        [Italian, English, "fr", "es", "de", "ru", "pt", "nl", "ja", "el", "tr", "zh", "ca", "ro"];

    public static IReadOnlyList<string> Others { get; } = [.. All.Where(code => code is not Italian and not English)];

    public static string? Normalize(string? code)
    {
        var normalized = code?.Trim().ToLowerInvariant();
        return normalized is not null && All.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }

    public static bool IsSupported(string? code) => Normalize(code) is not null;
}
