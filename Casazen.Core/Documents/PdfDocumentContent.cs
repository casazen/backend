namespace Casazen.Core.Documents;

/// <summary>
/// Content of a generated PDF (LT-09, A7-14): a title and a sequence of blocks that <see cref="IPdfDocumentRenderer"/>
/// lays out on A4 pages, wrapping and paginating as needed. There is no length limit.
/// </summary>
/// <param name="Title">First heading of the document and its PDF title.</param>
/// <param name="Blocks">Body of the document, in reading order.</param>
public sealed record PdfDocumentContent(string Title, IReadOnlyList<PdfBlock> Blocks)
{
    /// <summary>Text printed diagonally, in light grey, behind the content of every page (for example "BOZZA"). Null: none.</summary>
    public string? Watermark { get; init; }

    /// <summary>Language of the document (BCP 47), written in the PDF catalog.</summary>
    public string Language { get; init; } = "it-IT";

    /// <summary>
    /// Document of <paramref name="title"/> and a plain text body: a blank line separates paragraphs, a single line
    /// break is kept inside the paragraph.
    /// </summary>
    public static PdfDocumentContent FromPlainText(string title, string body) => new(title, PdfBlocks.FromPlainText(body));
}

/// <summary>A block of <see cref="PdfDocumentContent"/>.</summary>
public abstract record PdfBlock;

/// <summary>Heading of a section: level 1 is the largest; levels deeper than 3 render as level 3.</summary>
public sealed record PdfHeading(string Text, int Level = 2) : PdfBlock;

/// <summary>Paragraph wrapped to the page width; <c>\n</c> inside <see cref="Text"/> is a line break.</summary>
public sealed record PdfParagraph(string Text, bool Bold = false) : PdfBlock;

/// <summary>
/// Table with a header row repeated on every page it spans. Each row has one cell per column; the text of a cell
/// wraps inside the cell.
/// </summary>
public sealed record PdfTable(IReadOnlyList<PdfTableColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) : PdfBlock;

/// <summary>Column of a <see cref="PdfTable"/>: header, width relative to the other columns, alignment of the cells.</summary>
public sealed record PdfTableColumn(string Header, double RelativeWidth = 1, PdfCellAlignment Alignment = PdfCellAlignment.Left);

/// <summary>Horizontal alignment of the cells of a column (right for amounts).</summary>
public enum PdfCellAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>Helpers to build <see cref="PdfBlock"/> lists.</summary>
public static class PdfBlocks
{
    /// <summary>
    /// Paragraphs of a plain text: blank lines separate paragraphs, single line breaks stay inside the paragraph,
    /// <c>\r</c> is ignored. No character is dropped or truncated.
    /// </summary>
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
