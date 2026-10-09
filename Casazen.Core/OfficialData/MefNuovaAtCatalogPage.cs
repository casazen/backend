using System.Text.RegularExpressions;
using Casazen.Core.Entities;

namespace Casazen.Core.OfficialData;

/// <summary>
/// Reads the MEF tourist-tax catalog page for the permalink of the daily <c>nuova_at</c> CSV. If the wording
/// changes, parsing fails and the caller uses the configured fallback URL — nothing is invented.
/// </summary>
public static partial class MefNuovaAtCatalogPage
{
    public const string Authority = "MEF — archivio atti nuova_at";

    public static bool TryParse(string html, out Uri csvUrl)
    {
        csvUrl = null!;
        if (string.IsNullOrWhiteSpace(html))
            return false;

        var match = CsvPermalinkRegex().Match(html);
        if (!match.Success)
            return false;

        return OfficialHostAllowlist.TryCreateAllowed(match.Groups[1].Value, out csvUrl);
    }

    [GeneratedRegex(
        @"href=""(https://(?:www1\.)?finanze\.gov\.it/[^""]*nuova_at[^""]*\.(?:csv|txt))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CsvPermalinkRegex();
}

/// <summary>Dataset keys used by the comune official-profile agent (MEF <c>nuova_at</c>).</summary>
public static class MefOfficialSource
{
    public const string Authority = MefNuovaAtCatalogPage.Authority;

    public static string IndexDataset => OfficialSourceDatasets.MefNuovaAtIndex;

    public static string ActDataset => OfficialSourceDatasets.MefImpostaSoggiorno;
}
