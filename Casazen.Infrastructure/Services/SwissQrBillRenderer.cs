using System.Text;
using Casazen.Core.Documents;
using Casazen.Infrastructure.Documents;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using QRCoder;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The payment part of a Swiss QR-bill (E14/F01/US01/T02, NF-113), drawn at the bottom of an A4 page added at the end of a PDF: the receipt (62 mm) and the
/// payment part (148 mm), 105 mm high, as the Swiss Implementation Guidelines for the QR-bill lay them out, with the Swiss QR Code of 46 × 46 mm (error
/// correction M, made by QRCoder, MIT) and the Swiss cross of 7 × 7 mm in its middle.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Drawn, not flowed.</b> The guidelines place every element in millimetres, so the part is drawn with the drawing layer of the PDF library of
/// <see cref="MigraDocPdfDocumentRenderer"/> (PDFsharp, the same package): it is called by that renderer, under its lock and with its embedded font, never on its
/// own (PDFsharp is not safe for two documents at a time).</item>
/// <item><b>The QR Code</b> is vector: one black rectangle for each run of dark modules of the matrix of QRCoder, without its quiet zone (the 5 mm around the
/// code stay blank).</item>
/// <item><b>Electronic bill.</b> The lines between the parts are drawn with «Da staccare prima del versamento» above the payment part, as the guidelines ask
/// for a bill sent as a PDF. The font is the embedded DejaVu Sans of the documents: the guidelines name Arial, Frutiger, Helvetica or Liberation Sans
/// (a known gap until one of them is committed, see the runbook).</item>
/// </list>
/// </remarks>
internal static class SwissQrBillRenderer
{
    private const double PageWidthMm = 210;
    private const double PageHeightMm = 297;
    private const double SlipHeightMm = 105;
    private const double ReceiptWidthMm = 62;
    private const double MarginMm = 5;
    private const double QrSizeMm = 46;
    private const double CrossSizeMm = 7;

    /// <summary>The top of the slip on the page.</summary>
    private const double SlipTopMm = PageHeightMm - SlipHeightMm;

    /// <summary>Appends a page with the payment part of <paramref name="bill"/> to <paramref name="pdf"/>.</summary>
    public static void AddPaymentPart(PdfDocument pdf, PdfSwissQrBill bill)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(bill);

        var page = pdf.AddPage();
        page.Width = XUnit.FromMillimeter(PageWidthMm);
        page.Height = XUnit.FromMillimeter(PageHeightMm);
        using var gfx = XGraphics.FromPdfPage(page);

        DrawSeparation(gfx, bill.Labels);
        DrawReceipt(gfx, bill);
        DrawPaymentPart(gfx, bill);
    }

    private static void DrawSeparation(XGraphics gfx, PdfSwissQrBillLabels labels)
    {
        var pen = new XPen(XColors.Black, 0.5) { DashStyle = XDashStyle.Dash };
        gfx.DrawLine(pen, Pt(0), Pt(SlipTopMm), Pt(PageWidthMm), Pt(SlipTopMm));
        gfx.DrawLine(pen, Pt(ReceiptWidthMm), Pt(SlipTopMm), Pt(ReceiptWidthMm), Pt(PageHeightMm));

        var font = Font(7, bold: false);
        var text = labels.SeparateBeforePayingIn;
        var width = gfx.MeasureString(text, font).Width;
        gfx.DrawString(text, font, XBrushes.Black, new XPoint((Pt(PageWidthMm) - width) / 2, Pt(SlipTopMm - 5)), XStringFormats.TopLeft);
    }

    private static void DrawReceipt(XGraphics gfx, PdfSwissQrBill bill)
    {
        var labels = bill.Labels;
        var x = MarginMm;
        var width = ReceiptWidthMm - (2 * MarginMm);
        Title(gfx, labels.Receipt, x, SlipTopMm + MarginMm);

        var heading = Font(6, bold: true);
        var value = Font(8, bold: false);
        var y = SlipTopMm + 12;
        y = Block(gfx, labels.AccountPayableTo, [bill.Account, .. bill.Creditor], x, y, width, heading, value);
        if (bill.Reference is { } reference)
            y = Block(gfx, labels.Reference, [reference], x, y, width, heading, value);
        if (bill.Debtor is { } debtor)
            Block(gfx, labels.PayableBy, debtor, x, y, width, heading, value);
        else
            BlankBox(gfx, labels.PayableByNameAddress, x, y, 52, 20, heading);

        AmountSection(gfx, bill, x, x + 17, SlipTopMm + 68, heading, value, boxWidthMm: 30, boxHeightMm: 10);

        var acceptance = labels.AcceptancePoint;
        var acceptanceWidth = gfx.MeasureString(acceptance, heading).Width;
        gfx.DrawString(acceptance, heading, XBrushes.Black, new XPoint(Pt(ReceiptWidthMm - MarginMm) - acceptanceWidth, Pt(SlipTopMm + 82)), XStringFormats.TopLeft);
    }

    private static void DrawPaymentPart(XGraphics gfx, PdfSwissQrBill bill)
    {
        var labels = bill.Labels;
        var left = ReceiptWidthMm + MarginMm;
        Title(gfx, labels.PaymentPart, left, SlipTopMm + MarginMm);
        DrawQrCode(gfx, bill.Payload, left, SlipTopMm + 17);

        var heading = Font(8, bold: true);
        var value = Font(10, bold: false);
        AmountSection(gfx, bill, left, left + 20, SlipTopMm + 68, heading, value, boxWidthMm: 40, boxHeightMm: 15);

        var x = ReceiptWidthMm + 56;
        var width = PageWidthMm - x - MarginMm;
        var y = SlipTopMm + MarginMm;
        y = Block(gfx, labels.AccountPayableTo, [bill.Account, .. bill.Creditor], x, y, width, heading, value);
        if (bill.Reference is { } reference)
            y = Block(gfx, labels.Reference, [reference], x, y, width, heading, value);
        if (bill.AdditionalInformation is { } information)
            y = Block(gfx, labels.AdditionalInformation, [information], x, y, width, heading, value);
        if (bill.Debtor is { } debtor)
            Block(gfx, labels.PayableBy, debtor, x, y, width, heading, value);
        else
            BlankBox(gfx, labels.PayableByNameAddress, x, y, 65, 25, heading);
    }

    /// <summary>The Swiss QR Code of <paramref name="payload"/> (error correction M) at the given corner, 46 × 46 mm, with the Swiss cross in its middle.</summary>
    private static void DrawQrCode(XGraphics gfx, string payload, double leftMm, double topMm)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        var matrix = data.ModuleMatrix;
        var modules = 17 + (4 * data.Version);
        var quiet = (matrix.Count - modules) / 2;
        var size = Pt(QrSizeMm) / modules;
        var left = Pt(leftMm);
        var top = Pt(topMm);

        for (var row = 0; row < modules; row++)
        {
            var line = matrix[row + quiet];
            var column = 0;
            while (column < modules)
            {
                if (!line[column + quiet])
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < modules && line[column + quiet])
                    column++;

                // A hair of overlap so two rectangles that touch leave no seam on screen.
                gfx.DrawRectangle(XBrushes.Black, left + (start * size), top + (row * size), ((column - start) * size) + 0.05, size + 0.05);
            }
        }

        DrawSwissCross(gfx, leftMm + ((QrSizeMm - CrossSizeMm) / 2), topMm + ((QrSizeMm - CrossSizeMm) / 2));
    }

    /// <summary>The Swiss cross of the QR-bill: a white square of 7 mm, a black square inside it and the white cross (arms one sixth longer than wide).</summary>
    private static void DrawSwissCross(XGraphics gfx, double leftMm, double topMm)
    {
        gfx.DrawRectangle(XBrushes.White, Pt(leftMm), Pt(topMm), Pt(CrossSizeMm), Pt(CrossSizeMm));
        const double Border = 0.5;
        var inner = CrossSizeMm - (2 * Border);
        gfx.DrawRectangle(XBrushes.Black, Pt(leftMm + Border), Pt(topMm + Border), Pt(inner), Pt(inner));

        var arm = inner * 6 / 32;
        var length = inner * 20 / 32;
        var centerX = leftMm + (CrossSizeMm / 2);
        var centerY = topMm + (CrossSizeMm / 2);
        gfx.DrawRectangle(XBrushes.White, Pt(centerX - (arm / 2)), Pt(centerY - (length / 2)), Pt(arm), Pt(length));
        gfx.DrawRectangle(XBrushes.White, Pt(centerX - (length / 2)), Pt(centerY - (arm / 2)), Pt(length), Pt(arm));
    }

    /// <summary>The currency and the amount: the headings, then the values, or a box to fill in when the bill has no amount.</summary>
    private static void AmountSection(
        XGraphics gfx,
        PdfSwissQrBill bill,
        double currencyMm,
        double amountMm,
        double topMm,
        XFont heading,
        XFont value,
        double boxWidthMm,
        double boxHeightMm)
    {
        var labels = bill.Labels;
        gfx.DrawString(labels.Currency, heading, XBrushes.Black, new XPoint(Pt(currencyMm), Pt(topMm)), XStringFormats.TopLeft);
        gfx.DrawString(labels.Amount, heading, XBrushes.Black, new XPoint(Pt(amountMm), Pt(topMm)), XStringFormats.TopLeft);
        var valueTop = topMm + Mm(heading.Height) + 1;
        gfx.DrawString(bill.Currency, value, XBrushes.Black, new XPoint(Pt(currencyMm), Pt(valueTop)), XStringFormats.TopLeft);
        if (bill.Amount is { } amount)
            gfx.DrawString(amount, value, XBrushes.Black, new XPoint(Pt(amountMm), Pt(valueTop)), XStringFormats.TopLeft);
        else
            gfx.DrawRectangle(new XPen(XColors.Black, 0.75), Pt(amountMm), Pt(valueTop), Pt(boxWidthMm), Pt(boxHeightMm));
    }

    /// <summary>A heading and its lines (each wrapped to the width); answers where the next block starts.</summary>
    private static double Block(
        XGraphics gfx,
        string title,
        IReadOnlyList<string> lines,
        double leftMm,
        double topMm,
        double widthMm,
        XFont heading,
        XFont value)
    {
        gfx.DrawString(title, heading, XBrushes.Black, new XPoint(Pt(leftMm), Pt(topMm)), XStringFormats.TopLeft);
        var y = topMm + Mm(heading.Height) + 0.5;
        foreach (var line in lines.SelectMany(l => Wrap(gfx, l, value, Pt(widthMm))))
        {
            gfx.DrawString(line, value, XBrushes.Black, new XPoint(Pt(leftMm), Pt(y)), XStringFormats.TopLeft);
            y += Mm(value.Height);
        }

        return y + 2.5;
    }

    /// <summary>A heading and an empty box with its corners marked, for the payer to write in.</summary>
    private static void BlankBox(XGraphics gfx, string title, double leftMm, double topMm, double widthMm, double heightMm, XFont heading)
    {
        gfx.DrawString(title, heading, XBrushes.Black, new XPoint(Pt(leftMm), Pt(topMm)), XStringFormats.TopLeft);
        var top = topMm + Mm(heading.Height) + 1;
        var pen = new XPen(XColors.Black, 0.75);
        const double Corner = 3;
        var right = leftMm + widthMm;
        var bottom = top + heightMm;
        foreach (var (x, y, dx, dy) in new[] { (leftMm, top, 1, 1), (right, top, -1, 1), (leftMm, bottom, 1, -1), (right, bottom, -1, -1) })
        {
            gfx.DrawLine(pen, Pt(x), Pt(y), Pt(x + (dx * Corner)), Pt(y));
            gfx.DrawLine(pen, Pt(x), Pt(y), Pt(x), Pt(y + (dy * Corner)));
        }
    }

    private static void Title(XGraphics gfx, string text, double leftMm, double topMm) =>
        gfx.DrawString(text, Font(11, bold: true), XBrushes.Black, new XPoint(Pt(leftMm), Pt(topMm)), XStringFormats.TopLeft);

    /// <summary><paramref name="text"/> broken at the spaces into lines no wider than <paramref name="width"/> points; a word wider than a line is cut.</summary>
    private static IEnumerable<string> Wrap(XGraphics gfx, string text, XFont font, double width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (gfx.MeasureString(candidate, font).Width <= width)
            {
                line.Clear().Append(candidate);
                continue;
            }

            if (line.Length > 0)
            {
                yield return line.ToString();
                line.Clear();
            }

            var rest = word;
            while (gfx.MeasureString(rest, font).Width > width && rest.Length > 1)
            {
                var cut = rest.Length - 1;
                while (cut > 1 && gfx.MeasureString(rest[..cut], font).Width > width)
                    cut--;
                yield return rest[..cut];
                rest = rest[cut..];
            }

            line.Append(rest);
        }

        if (line.Length > 0)
            yield return line.ToString();
    }

    private static XFont Font(double size, bool bold) =>
        new(EmbeddedFontResolver.FamilyName, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    private static double Pt(double millimetres) => XUnit.FromMillimeter(millimetres).Point;

    private static double Mm(double points) => XUnit.FromPoint(points).Millimeter;
}
