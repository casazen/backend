namespace Casazen.Core.Documents;

public sealed record PdfDocumentContent(string Title, IReadOnlyList<PdfBlock> Blocks)
{
    public string? Watermark { get; init; }

    public string Language { get; init; } = "it-IT";

    public PdfSwissQrBill? QrBill { get; init; }

    public static PdfDocumentContent FromPlainText(string title, string body) => new(title, PdfBlocks.FromPlainText(body));
}

public abstract record PdfBlock;

public sealed record PdfHeading(string Text, int Level = 2, bool NewPage = false) : PdfBlock;

public sealed record PdfParagraph(string Text, bool Bold = false) : PdfBlock;

public sealed record PdfTable(IReadOnlyList<PdfTableColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) : PdfBlock;

public sealed record PdfTableColumn(string Header, double RelativeWidth = 1, PdfCellAlignment Alignment = PdfCellAlignment.Left);

public sealed record PdfSwissQrBill(
    string Payload,
    string Account,
    IReadOnlyList<string> Creditor,
    string? Reference,
    string? AdditionalInformation,
    IReadOnlyList<string>? Debtor,
    string Currency,
    string? Amount,
    PdfSwissQrBillLabels Labels);

public sealed record PdfSwissQrBillLabels(
    string Receipt,
    string PaymentPart,
    string AccountPayableTo,
    string Reference,
    string AdditionalInformation,
    string PayableBy,
    string PayableByNameAddress,
    string Currency,
    string Amount,
    string AcceptancePoint,
    string SeparateBeforePayingIn);

public enum PdfCellAlignment
{
    Left,
    Center,
    Right,
}

public static class PdfBlocks
{
    public static IReadOnlyList<PdfBlock> FromPlainText(string text)
    {
        var blocks = new List<PdfBlock>();
        var current = new List<string>();
        foreach (var line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            current.Add(line.TrimEnd());
        }

        Flush();
        return blocks;

        void Flush()
        {
            if (current.Count == 0)
                return;
            blocks.Add(new PdfParagraph(string.Join('\n', current)));
            current.Clear();
        }
    }
}
