using System.Text.RegularExpressions;

namespace Casazen.Core.Services;

/// <summary>
/// The Google Tag Manager container id of an agency's booking pages (NF-149, E40/F01/US01/T04): the single source of truth of its format, as
/// <c>CinFormat</c> is for the CIN; no other regex of it exists in the backend, and the frontend mirrors this rule (E41 names this file). After
/// trimming and upper-casing an id is <c>GTM-</c> followed by 4 to 12 ASCII letters or digits; an empty value clears the id. A GA4 id
/// (<c>G-…</c>), a Universal Analytics id (<c>UA-…</c>), a URL, a script tag, spaces or a longer text are refused.
/// </summary>
public static partial class GtmContainerIds
{
    /// <summary>Longest valid id: <c>GTM-</c> and 12 letters or digits.</summary>
    public const int MaxLength = 16;

    /// <summary>The stable code of a refused id (400).</summary>
    public const string InvalidCode = "gtm_container_id_invalid";

    /// <summary>
    /// The id to store for <paramref name="input"/>: <c>true</c> with the upper-case id, or with <c>null</c> for an empty value (the id is cleared);
    /// <c>false</c> for anything that is not a container id (then <paramref name="normalized"/> is <c>null</c>).
    /// </summary>
    public static bool TryNormalize(string? input, out string? normalized)
    {
        normalized = null;
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return true;

        // ASCII only before the upper-casing: a letter of another alphabet must not turn into a Latin one (the Turkish dotless i becomes I).
        if (trimmed.Length > MaxLength || !trimmed.All(char.IsAscii))
            return false;

        var candidate = trimmed.ToUpperInvariant();
        if (!ContainerId().IsMatch(candidate))
            return false;

        normalized = candidate;
        return true;
    }

    /// <summary>
    /// The id the agency's pages load (<c>resolve-host</c>): the stored one only while <c>SiteEmbed</c> is on and it is a valid id, otherwise
    /// <c>null</c>, and then the pages load nothing.
    /// </summary>
    public static string? Served(string? stored, bool siteEmbed) =>
        siteEmbed && TryNormalize(stored, out var id) ? id : null;

    [GeneratedRegex(@"^GTM-[A-Z0-9]{4,12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerId();
}
