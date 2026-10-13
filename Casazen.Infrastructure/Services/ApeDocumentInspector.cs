using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public sealed class ApeDocumentInspector : IApeDocumentInspector
{
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

        return hasTitle && hasSupporting
            ? new ApeInspectionResult(ApeInspectionStatus.Valid)
            : new ApeInspectionResult(ApeInspectionStatus.ContentMismatch);
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
