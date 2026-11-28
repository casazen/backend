using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Casazen.Tests.Unit.Documents;

internal static partial class PdfTestReader
{
    private const double FooterTop = 60;

    private const double WatermarkMinSize = 40;

    public static string Text(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join(' ', document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
    }

    public static IReadOnlyList<string> BodyWords(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages()
            .SelectMany(p => p.GetWords())
            .Where(w => w.BoundingBox.Bottom > FooterTop && !IsWatermark(w))
            .Select(w => w.Text)
            .ToList();
    }

    public static IReadOnlyList<PdfTestPage> Pages(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().Select(p =>
        {
            return new PdfTestPage(
                p.Width,
                p.Height,
                string.Join(' ', p.GetWords().Where(w => !IsWatermark(w)).Select(w => w.Text)),
                string.Concat(p.Letters.Where(IsWatermark).Select(l => l.Value)));
        }).ToList();
    }

    public static double MaxTextRight(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages()
            .SelectMany(p => p.Letters)
            .Where(l => !IsWatermark(l) && !string.IsNullOrWhiteSpace(l.Value))
            .Max(l => l.BoundingBox.Right);
    }

    public static IReadOnlyList<string> FontNames(byte[] pdf) =>
        BaseFontPattern().Matches(Latin1(pdf))
            .Select(m => m.Groups[1].Value.Replace("#20", " ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static (int Descriptors, int EmbeddedPrograms) FontEmbedding(byte[] pdf)
    {
        var raw = Latin1(pdf);
        return (FontDescriptorPattern().Count(raw), FontFile2Pattern().Count(raw));
    }

    private static bool IsWatermark(Word word) => word.Letters.Count > 0 && word.Letters.All(IsWatermark);

    private static bool IsWatermark(Letter letter) => letter.PointSize >= WatermarkMinSize || letter.TextOrientation != TextOrientation.Horizontal;

    private static string Latin1(byte[] pdf) => System.Text.Encoding.Latin1.GetString(pdf);

    [GeneratedRegex(@"/BaseFont\s*/(?:[A-Z]{6}\+)?([^\s/<>\[\]()]+)")]
    private static partial Regex BaseFontPattern();

    [GeneratedRegex(@"/Type\s*/FontDescriptor\b")]
    private static partial Regex FontDescriptorPattern();

    [GeneratedRegex(@"/FontFile2\s")]
    private static partial Regex FontFile2Pattern();
}

internal sealed record PdfTestPage(double Width, double Height, string Text, string Watermark);
