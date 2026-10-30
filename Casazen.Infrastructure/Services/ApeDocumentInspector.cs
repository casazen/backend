using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

public sealed class ApeDocumentInspector(TimeProvider? timeProvider = null) : IApeDocumentInspector
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly string[] TitleMarkers =
    [
        "ATTESTATO DI PRESTAZIONE ENERGETICA",
        "ATTESTATO PRESTAZIONE ENERGETICA",
        "ATTESTAZIONE DI PRESTAZIONE ENERGETICA",
        "ATTESTATO DI CERTIFICAZIONE ENERGETICA"
    ];

    private static readonly string[] SupportingMarkers =
    [
        "CLASSE ENERGETICA",
        "EPGL",
        "EPNREN",
        "EP NREN",
        "SIAPE",
        "CERTIFICATORE ENERGETICO",
        "DLGS 192",
        "D LGS 192",
        "DECRETO LEGISLATIVO 192"
    ];

    public ApeInspectionResult Inspect(Stream content)
    {
        if (content is null || !content.CanRead)
            return new ApeInspectionResult(ApeInspectionStatus.Unreadable);

        using var copy = new MemoryStream();
        if (content.CanSeek)
            content.Position = 0;
        content.CopyTo(copy);
        if (copy.Length == 0)
            return new ApeInspectionResult(ApeInspectionStatus.Unreadable);

        var bytes = copy.ToArray();
        if (!HasPdfHeader(bytes))
            return new ApeInspectionResult(ApeInspectionStatus.NotAPdf);

        var extracted = PdfLiteralTextExtractor.Extract(bytes);
        return InspectExtractedText(extracted);
    }

    // The labels as printed and as the OCR of a photo often leaves them ("DATA DI EMISSION", "EMISSIONS"); "EMISSIONI DI CO2" is not
    // followed by a date, so it never gives one. Lombardy prints "VALIDO FINO A", the national model "VALIDO FINO AL".
    private const string IssueLabel = @"(?:DATA DI EMISSIONE|DATA EMISSIONE|RILASCIATO IL|DATA DI RILASCIO|EMISSION[A-Z]{0,3})";
    private const string ExpiryLabel = @"(?:VALIDO FINO AL?|VALIDITA FINO AL?|DATA DI SCADENZA|DATA SCADENZA|SCADENZA)";
    private const string CodeLabel = @"(?:CODICE IDENTIFICATIVO(?: ATTESTATO| APE)?|CODICE ATTESTATO|NUMERO ATTESTATO|CODICE CERTIFICATO)";

    /// <summary>D.Lgs. 192/2005 art. 6 c. 5: an APE is valid ten years at most from its issue.</summary>
    private const int MaxValidityYears = 10;

    public ApeInspectionResult InspectExtractedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ApeInspectionResult(ApeInspectionStatus.ContentMismatch);

        var normalized = Normalize(text);
        var hasTitle = TitleMarkers.Any(m => normalized.Contains(m, StringComparison.Ordinal));
        var hasSupporting = SupportingMarkers.Any(m => normalized.Contains(m, StringComparison.Ordinal));
        var today = _clock.TodayInRomeAsDateOnly();
        var reading = Read(text, normalized, today);

        if (!hasTitle || !hasSupporting)
            return new(ApeInspectionStatus.ContentMismatch) { Reading = reading };

        var missing = new List<string>();
        bool Matches(string pattern) => Regex.IsMatch(normalized, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (reading.EnergyClass is null) missing.Add("EnergyClass");
        if (!Matches(@"EP ?GL(?: NREN| NR?EN| GLOBALE)? [0-9]+(?: [0-9]+)?")) missing.Add("Performance");
        if (reading.CertificateCode is null) missing.Add("CertificateCode");
        if (!Matches(@"INDIRIZZO [A-Z0-9]+")) missing.Add("Address");
        if (!Matches(@"(?:SOGGETTO CERTIFICATORE|CERTIFICATORE ENERGETICO|NOME E COGNOME) [A-Z]{2,} [A-Z]{2,}")) missing.Add("Certifier");
        var issue = reading.IssueDate;
        var expiry = reading.ExpiryDate;
        if (issue is null || issue > today) missing.Add("IssueDate");
        if (expiry is null || (issue is not null && expiry <= issue)) missing.Add("ExpiryDate");
        if (missing.Count > 0) return new(ApeInspectionStatus.Incomplete, missing) { Reading = reading };
        return expiry < today
            ? new(ApeInspectionStatus.Expired) { Reading = reading }
            : new(ApeInspectionStatus.Valid) { Reading = reading };
    }

    /// <summary>
    /// The data of the certificate in <paramref name="text"/> (the raw text, for the code with its separators) and
    /// <paramref name="normalized"/>. The energy class is the letter read most often after "CLASSE ENERGETICA": the reader of a photo
    /// reads the box of the class several times (the page, its negative, the crop around the label) and may get one of them wrong.
    /// A date that cannot be one of an APE (a misread year) is passed over; with no "valido fino al" at all (unreadable on the
    /// photo) the expiry is the issue plus ten years, the longest an APE lasts, while a label with an impossible date stays unread.
    /// </summary>
    private static ApeReading Read(string text, string normalized, DateOnly today)
    {
        var classes = Regex.Matches(normalized, @"CLASSE ENERGETICA (A4|A3|A2|A1|A PLUS|[A-G])(?: |$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(m => m.Groups[1].Value == "A PLUS" ? "A+" : m.Groups[1].Value)
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var issue = ReadDate(normalized, IssueLabel, 1990, today.Year);
        var expiry = ReadDate(normalized, ExpiryLabel, 1990, 2100);
        var expiryLabelPresent = Regex.IsMatch(normalized, ExpiryLabel + " [0-9]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (expiry is null && !expiryLabelPresent && issue is { } issued)
            expiry = issued.AddYears(MaxValidityYears);

        var index = Regex.Match(normalized, @"EP ?GL ?N ?REN ([0-9]{1,4})(?: ([0-9]{1,2}))?(?: |$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        decimal? globalNonRenewable = index.Success && decimal.TryParse(
            index.Groups[2].Success ? $"{index.Groups[1].Value}.{index.Groups[2].Value}" : index.Groups[1].Value,
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;

        return new ApeReading(
            ReadCode(text, normalized),
            classes,
            issue,
            expiry,
            globalNonRenewable,
            normalized.Contains("REGIONE LOMBARDIA", StringComparison.Ordinal) || normalized.Contains("CENED", StringComparison.Ordinal));
    }

    /// <summary>The code after its label, with its separators as printed ("12345-000678/19"); null when there is no label with a code.</summary>
    private static string? ReadCode(string text, string normalized)
    {
        if (!Regex.IsMatch(normalized, CodeLabel + " [A-Z0-9]*[0-9][A-Z0-9]*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            return null;
        var flat = Regex.Replace(Normalize(text, keepSeparators: true), @"\s+", " ");
        var match = Regex.Match(flat, CodeLabel + @"[ :.\-]{0,4}([A-Z0-9][A-Z0-9/.\-]*[0-9][A-Z0-9/.\-]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return null;
        var code = match.Groups[1].Value.Trim('.', '-', '/');
        return code.Length is >= 4 and <= 40 ? code : null;
    }

    private static DateOnly? ReadDate(string text, string label, int minYear, int maxYear)
    {
        foreach (Match match in Regex.Matches(text, label + @" (\d{1,2}) (\d{1,2}) (\d{4})(?: |$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            if (DateOnly.TryParseExact($"{match.Groups[1].Value.PadLeft(2, '0')}/{match.Groups[2].Value.PadLeft(2, '0')}/{match.Groups[3].Value}", "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date.Year >= minYear && date.Year <= maxYear)
                return date;
        }
        return null;
    }

    public async Task<ApeInspectionResult> InspectAsync(Stream content, CancellationToken ct = default)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        if (copy.Length == 0 || copy.Length > 10 * 1024 * 1024)
            return new(ApeInspectionStatus.Unreadable);
        var bytes = copy.ToArray();
        var isPdf = HasPdfHeader(bytes);
        if (!isPdf && ImageSignature.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, ImageSignature.HeaderLength))) is not ("image/jpeg" or "image/png"))
            return new(ApeInspectionStatus.NotAPdf);
        // Real PDFs need a layout-aware reader; literal extraction alone misses embedded fonts.
        try
        {
            var text = await ApeOcrReader.ReadAsync(bytes, isPdf, InspectExtractedText, ct);
            return InspectExtractedText(text);
        }
        catch (ApeOcrUnavailableException) { return new(ApeInspectionStatus.OcrUnavailable); }
        catch (InvalidDataException) { return new(ApeInspectionStatus.Unreadable); }
    }

    /// <param name="keepSeparators">Keep <c>/ - . :</c> (the separators of a code such as "12345-000678/19") instead of spaces.</param>
    internal static string Normalize(string text, bool keepSeparators = false)
    {
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : (keepSeparators && c is '/' or '-' or '.' or ':') ? c : ' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static bool HasPdfHeader(byte[] bytes)
    {
        var probe = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8));
        return probe.Contains("%PDF", StringComparison.Ordinal);
    }
}
