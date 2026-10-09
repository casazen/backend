using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.OfficialData;

/// <summary>
/// Daily CSV of the MEF archive <c>nuova_at</c>: municipal tax acts, not a national list of amounts.
/// Matching is by cadastral code, then by unique name+province; 0 or &gt;1 rows without a cadastral code is rejected.
/// </summary>
public static class MefNuovaAtIndex
{
    public const string Authority = MefNuovaAtCatalogPage.Authority;

    public sealed record Snapshot(string Sha256, string SourceUrl, IReadOnlyList<Row> Rows);

    public sealed record Row(
        string? CadastralCode,
        string ComuneName,
        string Province,
        string Tributo,
        string ActType,
        string ActNumber,
        DateOnly? ActDate,
        DateOnly? MefPublishedOn,
        string PdfUrl,
        bool IsAttachment);

    public sealed record MatchResult
    {
        public bool Succeeded { get; init; }
        public string? Rejection { get; init; }
        public string ActId { get; init; } = string.Empty;
        public DateOnly? MefPublishedOn { get; init; }
        public string PdfUrl { get; init; } = string.Empty;
        public string ActSha256 { get; init; } = string.Empty;
        public IReadOnlyList<Row> Rows { get; init; } = [];

        public static MatchResult Reject(string reason) => new() { Succeeded = false, Rejection = reason };
    }

    public static Snapshot Parse(ReadOnlySpan<byte> csvBytes, string sha256, string sourceUrl)
    {
        var text = Encoding.UTF8.GetString(csvBytes);
        if (text.Contains('\uFFFD', StringComparison.Ordinal))
            text = Encoding.Latin1.GetString(csvBytes);

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return new Snapshot(sha256, sourceUrl, []);

        var delimiter = lines[0].Count(c => c == ';') >= lines[0].Count(c => c == ',') ? ';' : ',';
        var header = Split(lines[0], delimiter);
        var columns = MapColumns(header);
        var rows = new List<Row>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = Split(lines[i], delimiter);
            if (cells.Length == 0)
                continue;
            var tributo = Cell(cells, columns.Tributo);
            var cadastral = NormalizeCadastral(Cell(cells, columns.Cadastral));
            var url = Cell(cells, columns.Url);
            rows.Add(new Row(
                cadastral,
                Cell(cells, columns.Comune),
                Cell(cells, columns.Province),
                tributo,
                Cell(cells, columns.ActType),
                Cell(cells, columns.ActNumber),
                ParseDate(Cell(cells, columns.ActDate)),
                ParseDate(Cell(cells, columns.Published)),
                url,
                LooksLikeAttachment(Cell(cells, columns.ActType), url)));
        }

        return new Snapshot(sha256, sourceUrl, rows);
    }

    public static MatchResult Match(Snapshot snapshot, Comune comune)
    {
        var touristTax = snapshot.Rows.Where(IsTouristTax).ToList();
        List<Row> hits;
        if (!string.IsNullOrWhiteSpace(comune.CadastralCode))
        {
            var code = comune.CadastralCode.Trim().ToUpperInvariant();
            hits = touristTax.Where(r => string.Equals(r.CadastralCode, code, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            var name = ComuneNames.Normalize(comune.Name);
            var province = comune.ProvinceCode.Trim();
            hits = touristTax
                .Where(r => string.Equals(ComuneNames.Normalize(r.ComuneName), name, StringComparison.Ordinal))
                .ToList();
            if (!string.IsNullOrEmpty(province))
                hits = hits.Where(r => ProvinceMatches(r.Province, province)).ToList();
            var distinctComuni = hits
                .Select(r => ComuneNames.Normalize(r.ComuneName) + "|" + r.Province.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (distinctComuni != 1)
                return MatchResult.Reject(hits.Count == 0 ? "no_mef_row" : "ambiguous_mef_row");
        }

        if (hits.Count == 0)
            return MatchResult.Reject("no_mef_row");

        var latestDate = hits.Max(r => r.MefPublishedOn ?? DateOnly.MinValue);
        var latest = hits.Where(r => (r.MefPublishedOn ?? DateOnly.MinValue) == latestDate).ToList();
        var primary = latest.FirstOrDefault(r => r.IsAttachment)
            ?? latest.OrderByDescending(r => r.ActDate ?? DateOnly.MinValue).First();
        if (string.IsNullOrWhiteSpace(primary.PdfUrl)
            || !OfficialHostAllowlist.TryCreateAllowed(primary.PdfUrl, out var pdfUri))
            return MatchResult.Reject("pdf_url_not_allowed");

        var actId = FormatActId(latest);
        if (string.IsNullOrWhiteSpace(actId))
            return MatchResult.Reject("act_id_missing");

        return new MatchResult
        {
            Succeeded = true,
            ActId = actId,
            MefPublishedOn = primary.MefPublishedOn,
            PdfUrl = pdfUri.ToString(),
            ActSha256 = HashActSet(latest),
            Rows = latest,
        };
    }

    public static string HashActSet(IEnumerable<Row> rows)
    {
        var tokens = rows
            .Select(r => $"{FormatOneAct(r)}|{r.MefPublishedOn:yyyy-MM-dd}|{r.PdfUrl.Trim()}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        var payload = string.Join('\n', tokens);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool IsTouristTax(Row row)
    {
        var t = row.Tributo.Trim().ToLowerInvariant();
        return t.Contains("imposta di soggiorno", StringComparison.Ordinal)
            || t.Contains("contributo di soggiorno", StringComparison.Ordinal)
            || t.Contains("imposta / contributo di soggiorno", StringComparison.Ordinal);
    }

    public static string FormatActId(IEnumerable<Row> rows) =>
        string.Join("; ", rows
            .Select(FormatOneAct)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string FormatOneAct(Row row)
    {
        var kind = CompactActType(row.ActType);
        var number = row.ActNumber.Trim();
        if (string.IsNullOrEmpty(number))
            return string.Empty;
        return row.ActDate is { } date
            ? $"{kind} {number}/{date:yyyy-MM-dd}"
            : $"{kind} {number}";
    }

    private static string CompactActType(string actType)
    {
        var t = actType.Trim().ToLowerInvariant();
        if (t.Contains("giunta", StringComparison.Ordinal))
            return "Giunta";
        if (t.Contains("consiglio", StringComparison.Ordinal))
            return "Consiglio";
        if (t.Contains("allegato", StringComparison.Ordinal))
            return "Allegato";
        return string.IsNullOrWhiteSpace(actType) ? "Atto" : actType.Trim();
    }

    private static bool LooksLikeAttachment(string actType, string url)
    {
        var blob = actType + " " + url;
        return blob.Contains("allegato", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("tariff", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProvinceMatches(string csvProvince, string plateCode)
    {
        var left = csvProvince.Trim();
        if (left.Length == 2)
            return string.Equals(left, plateCode, StringComparison.OrdinalIgnoreCase);
        return left.Contains(plateCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeCadastral(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("N.d.", StringComparison.OrdinalIgnoreCase))
            return null;
        return ComuneRules.NormalizeCadastralCode(trimmed) ?? trimmed.ToUpperInvariant();
    }

    private static DateOnly? ParseDate(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return null;
        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso;
        if (DateOnly.TryParseExact(trimmed, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var it))
            return it;
        if (DateOnly.TryParseExact(trimmed, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dash))
            return dash;
        return null;
    }

    private readonly record struct ColumnMap(
        int Cadastral, int Comune, int Province, int Tributo, int ActType, int ActNumber, int ActDate, int Published, int Url);

    private static ColumnMap MapColumns(string[] header)
    {
        int Find(params string[] aliases)
        {
            for (var i = 0; i < header.Length; i++)
            {
                var name = header[i].Trim().ToLowerInvariant();
                foreach (var alias in aliases)
                {
                    if (name.Contains(alias, StringComparison.Ordinal))
                        return i;
                }
            }

            return -1;
        }

        return new ColumnMap(
            Find("codice catastale", "catastale", "belfiore"),
            Find("denominazione comune", "comune"),
            Find("provincia", "sigla"),
            Find("tributo", "imposta"),
            Find("tipo atto", "tipo atto", "atto"),
            Find("numero atto", "n. atto", "numero"),
            Find("data atto", "data delibera"),
            Find("data pubblicazione", "pubblicazione", "data pubbl"),
            Find("url", "link", "file", "pdf"));
    }

    private static string Cell(string[] cells, int index) =>
        index >= 0 && index < cells.Length ? cells[index].Trim().Trim('"') : string.Empty;

    private static string[] Split(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString());
        return result.ToArray();
    }
}
