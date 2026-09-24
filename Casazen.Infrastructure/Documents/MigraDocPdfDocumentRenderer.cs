using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Documents;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using MigraDocRenderer = MigraDoc.Rendering.PdfDocumentRenderer;

namespace Casazen.Infrastructure.Documents;

/// <summary>
/// <see cref="IPdfDocumentRenderer"/> on PDFsharp/MigraDoc 6 (MIT, fully managed: no native library, no system
/// font). A4 portrait, 2 cm side margins, "Pagina N di M" centred in the footer, paragraphs wrapped and split across
/// pages, table header rows repeated on each page. Text is written as Unicode with the embedded DejaVu Sans subset
/// (<see cref="EmbeddedFontResolver"/>), so the PDF carries a ToUnicode map and its text can be searched and copied.
/// Stateless and thread-safe: register it as a singleton.
/// </summary>
public sealed partial class MigraDocPdfDocumentRenderer : IPdfDocumentRenderer
{
    /// <summary>A4 in millimetres (ISO 216): 595 × 842 points.</summary>
    public const double PageWidthMm = 210;
    public const double PageHeightMm = 297;

    private const double SideMarginCm = 2;
    private const double TopMarginCm = 2;
    private const double BottomMarginCm = 2.5;
    private const double FooterDistanceCm = 1.2;
    private const double BodyFontSize = 10;
    private const double TableFontSize = 9;
    private const double CellPaddingPt = 4;

    private static readonly Color TableBorder = new(160, 160, 160);
    private static readonly Color TableHeaderShading = new(235, 235, 235);

    /// <summary>Light grey without transparency: PDF/A-1 forbids transparency, and it stays readable behind the text.</summary>
    private static readonly XColor WatermarkColor = XColor.FromArgb(222, 222, 222);

    /// <summary>Heading styles by level (1 = document title): font size and spacing in points, bold.</summary>
    private static readonly (string Name, double Size, double SpaceBefore, double SpaceAfter)[] HeadingStyles =
    [
        (StyleNames.Heading1, 15, 0, 12),
        (StyleNames.Heading2, 12, 10, 4),
        (StyleNames.Heading3, BodyFontSize, 8, 3),
    ];

    private static double BodyWidth => Unit.FromMillimeter(PageWidthMm).Point - (2 * Unit.FromCentimeter(SideMarginCm).Point);

    public byte[] Render(PdfDocumentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        EmbeddedFontResolver.EnsureRegistered();

        Document document;
        using (var words = new LongWordBreaker())
            document = BuildDocument(content, words);

        var renderer = new MigraDocRenderer { Document = document };
        renderer.RenderDocument();

        var pdf = renderer.PdfDocument;
        pdf.Info.Title = CleanText(content.Title);
        pdf.Info.Creator = "CasaZen";
        pdf.Language = content.Language;

        if (!string.IsNullOrWhiteSpace(content.Watermark))
        {
            foreach (var page in pdf.Pages)
                DrawWatermark(page, CleanText(content.Watermark));
        }

        using var output = new MemoryStream();
        pdf.Save(output, false);
        return output.ToArray();
    }

    private static Document BuildDocument(PdfDocumentContent content, LongWordBreaker words)
    {
        var document = new Document();
        document.Info.Title = CleanText(content.Title);
        document.Info.Author = "CasaZen";
        DefineStyles(document);

        var section = document.AddSection();
        var setup = section.PageSetup;
        setup.PageWidth = Unit.FromMillimeter(PageWidthMm);
        setup.PageHeight = Unit.FromMillimeter(PageHeightMm);
        setup.Orientation = Orientation.Portrait;
        setup.LeftMargin = Unit.FromCentimeter(SideMarginCm);
        setup.RightMargin = Unit.FromCentimeter(SideMarginCm);
        setup.TopMargin = Unit.FromCentimeter(TopMarginCm);
        setup.BottomMargin = Unit.FromCentimeter(BottomMarginCm);
        setup.FooterDistance = Unit.FromCentimeter(FooterDistanceCm);

        var footer = section.Footers.Primary.AddParagraph();
        footer.Style = StyleNames.Footer;
        footer.AddText("Pagina ");
        footer.AddPageField();
        footer.AddText(" di ");
        footer.AddNumPagesField();

        AddHeading(section, content.Title, level: 1, words);
        foreach (var block in content.Blocks)
        {
            switch (block)
            {
                case PdfHeading heading:
                    AddHeading(section, heading.Text, heading.Level, words);
                    break;
                case PdfParagraph paragraph:
                    var p = section.AddParagraph();
                    p.Style = StyleNames.Normal;
                    if (paragraph.Bold)
                        p.Format.Font.Bold = true;
                    AddText(p, words.Break(CleanText(paragraph.Text), BodyWidth, BodyFontSize, paragraph.Bold));
                    break;
                case PdfTable table:
                    AddTable(section, table, words);
                    break;
                default:
                    throw new ArgumentException($"Unsupported PDF block {block.GetType().Name}.", nameof(content));
            }
        }

        return document;
    }

    private static void DefineStyles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName;
        normal.Font.Size = BodyFontSize;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.15;
        normal.ParagraphFormat.WidowControl = true;

        foreach (var (name, size, spaceBefore, spaceAfter) in HeadingStyles)
        {
            var style = document.Styles[name]!;
            style.Font.Size = size;
            style.Font.Bold = true;
            style.ParagraphFormat.SpaceBefore = Unit.FromPoint(spaceBefore);
            style.ParagraphFormat.SpaceAfter = Unit.FromPoint(spaceAfter);
            style.ParagraphFormat.KeepWithNext = true;
        }

        var footer = document.Styles[StyleNames.Footer]!;
        footer.Font.Size = 8;
        footer.ParagraphFormat.Alignment = ParagraphAlignment.Center;
        footer.ParagraphFormat.SpaceAfter = 0;
    }

    private static void AddHeading(Section section, string text, int level, LongWordBreaker words)
    {
        var (name, size, _, _) = HeadingStyles[Math.Clamp(level, 1, HeadingStyles.Length) - 1];
        var paragraph = section.AddParagraph();
        paragraph.Style = name;
        AddText(paragraph, words.Break(CleanText(text), BodyWidth, size, bold: true));
    }

    private static void AddTable(Section section, PdfTable source, LongWordBreaker words)
    {
        if (source.Columns.Count == 0)
            throw new ArgumentException("A PDF table needs at least one column.", nameof(source));
        foreach (var values in source.Rows)
        {
            if (values.Count != source.Columns.Count)
                throw new ArgumentException(
                    $"Every table row needs {source.Columns.Count} cells; one has {values.Count}.", nameof(source));
        }

        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = TableBorder;
        table.LeftPadding = Unit.FromPoint(CellPaddingPt);
        table.RightPadding = Unit.FromPoint(CellPaddingPt);
        table.TopPadding = Unit.FromPoint(2);
        table.BottomPadding = Unit.FromPoint(2);
        table.Format.Font.Size = TableFontSize;
        table.Format.SpaceAfter = 0;

        var totalWeight = source.Columns.Sum(c => c.RelativeWidth > 0 ? c.RelativeWidth : 1);
        var textWidths = new double[source.Columns.Count];
        for (var i = 0; i < source.Columns.Count; i++)
        {
            var definition = source.Columns[i];
            var width = BodyWidth * (definition.RelativeWidth > 0 ? definition.RelativeWidth : 1) / totalWeight;
            textWidths[i] = width - (2 * CellPaddingPt) - 1;
            var column = table.AddColumn(Unit.FromPoint(width));
            column.Format.Alignment = definition.Alignment switch
            {
                PdfCellAlignment.Right => ParagraphAlignment.Right,
                PdfCellAlignment.Center => ParagraphAlignment.Center,
                _ => ParagraphAlignment.Left,
            };
        }

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        header.Shading.Color = TableHeaderShading;
        for (var i = 0; i < source.Columns.Count; i++)
            AddText(header.Cells[i].AddParagraph(), words.Break(CleanText(source.Columns[i].Header), textWidths[i], TableFontSize, bold: true));

        foreach (var values in source.Rows)
        {
            var row = table.AddRow();
            for (var i = 0; i < values.Count; i++)
                AddText(row.Cells[i].AddParagraph(), words.Break(CleanText(values[i]), textWidths[i], TableFontSize, bold: false));
        }

        // Space between the table and the next block.
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(2);
    }

    /// <summary>Text with its line breaks and tabs (<paramref name="text"/> is already cleaned).</summary>
    private static void AddText(Paragraph paragraph, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                paragraph.AddLineBreak();

            var segments = lines[i].Split('\t');
            for (var j = 0; j < segments.Length; j++)
            {
                if (j > 0)
                    paragraph.AddTab();
                if (segments[j].Length > 0)
                    paragraph.AddText(segments[j]);
            }
        }
    }

    /// <summary>No <c>\r</c>; control characters other than line break and tab become spaces.</summary>
    private static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\r')
                continue;
            sb.Append(c is '\n' or '\t' || !char.IsControl(c) ? c : ' ');
        }

        return sb.ToString();
    }

    private static void DrawWatermark(PdfPage page, string text)
    {
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
        var width = page.Width.Point;
        var height = page.Height.Point;
        var diagonal = Math.Sqrt((width * width) + (height * height));

        var probe = new XFont(EmbeddedFontResolver.FamilyName, 100, XFontStyleEx.Bold);
        var measured = gfx.MeasureString(text, probe).Width;
        var size = Math.Min(160, 100 * 0.7 * diagonal / Math.Max(measured, 1));
        var font = new XFont(EmbeddedFontResolver.FamilyName, size, XFontStyleEx.Bold);

        gfx.TranslateTransform(width / 2, height / 2);
        gfx.RotateTransform(-Math.Atan2(height, width) * 180 / Math.PI);
        gfx.DrawString(text, font, new XSolidBrush(WatermarkColor), new XPoint(0, 0), XStringFormats.Center);
    }

    [GeneratedRegex(@"[^ \n\t]+")]
    private static partial Regex WordPattern();

    /// <summary>
    /// MigraDoc breaks lines only at blanks: a word wider than the line (a long URL, an IBAN in a narrow column) would
    /// be drawn past the margin and cut off. Such a word is split into pieces that fit, one per line; nothing is
    /// added or dropped. One instance per rendering (measuring is not thread-safe).
    /// </summary>
    private sealed class LongWordBreaker : IDisposable
    {
        /// <summary>Margin for rounding and for the tolerance MigraDoc applies when it fits words.</summary>
        private const double Safety = 0.97;

        private readonly PdfDocument _scratch = new();
        private readonly XGraphics _measure;
        private readonly Dictionary<(double Size, bool Bold), XFont> _fonts = [];
        private readonly Dictionary<(double Size, bool Bold, string Element), double> _widths = [];

        public LongWordBreaker() => _measure = XGraphics.FromPdfPage(_scratch.AddPage());

        public string Break(string text, double availableWidth, double fontSize, bool bold)
        {
            var width = availableWidth * Safety;
            var font = Font(fontSize, bold);
            return WordPattern().Replace(text, match =>
                _measure.MeasureString(match.Value, font).Width <= width
                    ? match.Value
                    : Split(match.Value, width, fontSize, bold));
        }

        private string Split(string word, double width, double fontSize, bool bold)
        {
            var sb = new StringBuilder(word.Length + 8);
            var line = 0d;
            var elements = StringInfo.GetTextElementEnumerator(word);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var elementWidth = Width(element, fontSize, bold);
                if (line > 0 && line + elementWidth > width)
                {
                    sb.Append('\n');
                    line = 0;
                }

                sb.Append(element);
                line += elementWidth;
            }

            return sb.ToString();
        }

        private double Width(string element, double fontSize, bool bold)
        {
            var key = (fontSize, bold, element);
            if (!_widths.TryGetValue(key, out var width))
            {
                width = _measure.MeasureString(element, Font(fontSize, bold)).Width;
                _widths[key] = width;
            }

            return width;
        }

        private XFont Font(double size, bool bold)
        {
            if (!_fonts.TryGetValue((size, bold), out var font))
            {
                font = new XFont(EmbeddedFontResolver.FamilyName, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                _fonts[(size, bold)] = font;
            }

            return font;
        }

        public void Dispose()
        {
            _measure.Dispose();
            _scratch.Dispose();
        }
    }
}
