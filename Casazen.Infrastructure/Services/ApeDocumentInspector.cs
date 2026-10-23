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

    public ApeInspectionResult InspectExtractedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ApeInspectionResult(ApeInspectionStatus.ContentMismatch);

        var normalized = Normalize(text);
        var hasTitle = TitleMarkers.Any(m => normalized.Contains(m, StringComparison.Ordinal));
        var hasSupporting = SupportingMarkers.Any(m => normalized.Contains(m, StringComparison.Ordinal));

        if (!hasTitle || !hasSupporting)
            return new(ApeInspectionStatus.ContentMismatch);

        var missing = new List<string>();
        bool Matches(string pattern) => Regex.IsMatch(normalized, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!Matches(@"CLASSE ENERGETICA (?:[A-G]|A[1-4]|A PLUS)(?: |$)")) missing.Add("EnergyClass");
        if (!Matches(@"EP ?GL(?: NREN| NR?EN| GLOBALE)? [0-9]+(?: [0-9]+)?")) missing.Add("Performance");
        if (!Matches(@"(?:CODICE IDENTIFICATIVO(?: ATTESTATO| APE)?|CODICE ATTESTATO|NUMERO ATTESTATO|CODICE CERTIFICATO) [A-Z0-9]*[0-9][A-Z0-9]*")) missing.Add("CertificateCode");
        if (!Matches(@"INDIRIZZO [A-Z0-9]+")) missing.Add("Address");
        if (!Matches(@"(?:SOGGETTO CERTIFICATORE|CERTIFICATORE ENERGETICO|NOME E COGNOME) [A-Z]{2,} [A-Z]{2,}")) missing.Add("Certifier");
        var issue = ReadDate(normalized, @"(?:DATA DI EMISSIONE|DATA EMISSIONE|RILASCIATO IL|DATA DI RILASCIO)");
        var expiry = ReadDate(normalized, @"(?:VALIDO FINO AL|VALIDITA FINO AL|DATA DI SCADENZA|DATA SCADENZA|SCADENZA)");
        var today = _clock.TodayInRomeAsDateOnly();
        if (issue is null || issue > today) missing.Add("IssueDate");
        if (expiry is null || (issue is not null && expiry <= issue)) missing.Add("ExpiryDate");
        if (missing.Count > 0) return new(ApeInspectionStatus.Incomplete, missing);
        return expiry < today
            ? new(ApeInspectionStatus.Expired)
            : new(ApeInspectionStatus.Valid);
    }

    private static DateOnly? ReadDate(string text, string label)
    {
        var match = Regex.Match(text, label + @" (\d{1,2}) (\d{1,2}) (\d{4})(?: |$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success && DateOnly.TryParseExact($"{match.Groups[1].Value.PadLeft(2, '0')}/{match.Groups[2].Value.PadLeft(2, '0')}/{match.Groups[3].Value}", "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
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

    internal static string Normalize(string text)
    {
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static bool HasPdfHeader(byte[] bytes)
    {
        var probe = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8));
        return probe.Contains("%PDF", StringComparison.Ordinal);
    }
}
