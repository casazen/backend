using System.Globalization;
using System.Text.RegularExpressions;

namespace Casazen.Core.OfficialData;

/// <summary>
/// Reads the ISTAT classification page for the permalink of the comuni file and its "aggiornato al" date.
/// Nothing is invented: if the page wording changes, parsing fails and the job does not import.
/// </summary>
public static partial class IstatCatalogPage
{
    public const string IstatAuthority = "ISTAT";

    public sealed record Snapshot(DateOnly ReferenceDate, string? PermalinkUrl);

    public static bool TryParse(string html, out Snapshot snapshot)
    {
        snapshot = null!;
        if (string.IsNullOrWhiteSpace(html))
            return false;

        var dateMatch = AggiornatoAlRegex().Match(html);
        if (!dateMatch.Success || !TryParseItalianDate(dateMatch.Groups[1].Value, dateMatch.Groups[2].Value, dateMatch.Groups[3].Value, out var date))
            return false;

        string? permalink = null;
        var link = PermalinkRegex().Match(html);
        if (link.Success)
            permalink = link.Groups[1].Value;

        snapshot = new Snapshot(date, permalink);
        return true;
    }

    public static bool TryParseItalianDate(string day, string month, string year, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            || !int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y))
            return false;

        var monthNumber = month.Trim().ToLowerInvariant() switch
        {
            "gennaio" => 1,
            "febbraio" => 2,
            "marzo" => 3,
            "aprile" => 4,
            "maggio" => 5,
            "giugno" => 6,
            "luglio" => 7,
            "agosto" => 8,
            "settembre" => 9,
            "ottobre" => 10,
            "novembre" => 11,
            "dicembre" => 12,
            _ => 0,
        };
        if (monthNumber == 0)
            return false;

        try
        {
            date = new DateOnly(y, monthNumber, d);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"aggiornato al\s+(\d{1,2})\s+([A-Za-zÀ-ÿ]+)\s+(\d{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AggiornatoAlRegex();

    [GeneratedRegex(@"href=""(https://www\.istat\.it/storage/codici-unita-amministrative/Elenco-comuni-italiani\.(?:csv|xlsx))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PermalinkRegex();
}
