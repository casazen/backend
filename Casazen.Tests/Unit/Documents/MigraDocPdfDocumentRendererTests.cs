using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Infrastructure.Documents;
using Xunit;

namespace Casazen.Tests.Unit.Documents;

/// <summary>
/// LT-09 (A7-14): the single PDF renderer lays out A4 pages, wraps and paginates without dropping text, keeps every
/// Unicode character, draws the watermark on every page and embeds its own fonts.
/// </summary>
public class MigraDocPdfDocumentRendererTests
{
    private readonly MigraDocPdfDocumentRenderer _sut = new();

    [Fact]
    public void Render_AnyDocument_EveryPageIsA4()
    {
        var pdf = _sut.Render(new PdfDocumentContent("Titolo", LongParagraphs(words: 3000)));

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count > 1);
        Assert.All(pages, page =>
        {
            Assert.Equal(595, Math.Round(page.Width));
            Assert.Equal(842, Math.Round(page.Height));
        });
    }

    [Fact]
    public void Render_LongText_WrapsAndPaginatesWithoutLosingWords()
    {
        const int wordCount = 4000;
        var content = new PdfDocumentContent("Documento lungo", LongParagraphs(wordCount));
        Assert.True(content.Blocks.OfType<PdfParagraph>().Sum(p => p.Text.Length) > 20_000);

        var pdf = _sut.Render(content);

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count >= 3, $"{pages.Count} pages");
        var tokens = PdfTestReader.BodyWords(pdf).Where(w => w.StartsWith('w')).ToList();
        Assert.Equal(Enumerable.Range(1, wordCount).Select(Token), tokens);
        for (var i = 0; i < pages.Count; i++)
            Assert.Contains($"Pagina {i + 1} di {pages.Count}", pages[i].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_UnicodeText_IsPreservedInTheExtractedText()
    {
        string[] samples =
        [
            "àèìòù ÀÈÌÒÙ é",
            "Straße",
            "Łukasz Wałęsa",
            "Karel Čapek",
            "Jürgen Müller",
            "Ольга Иванова",
            "Σωκράτης",
            "€ 1.234,56 «virgolette» — trattino",
        ];

        var pdf = _sut.Render(new PdfDocumentContent(
            "Contratto – Müller",
            [new PdfParagraph(string.Join('\n', samples))]));

        var text = PdfTestReader.Text(pdf);
        Assert.Contains("Contratto – Müller", text, StringComparison.Ordinal);
        foreach (var sample in samples)
            Assert.Contains(sample, text, StringComparison.Ordinal);
        Assert.DoesNotContain("?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithWatermark_DrawsItOnEveryPage()
    {
        var pdf = _sut.Render(new PdfDocumentContent("Anteprima", LongParagraphs(words: 2500)) { Watermark = "BOZZA" });

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count > 1);
        Assert.All(pages, page => Assert.Equal("BOZZA", page.Watermark));
    }

    [Fact]
    public void Render_WithoutWatermark_HasNone()
    {
        var pdf = _sut.Render(new PdfDocumentContent("Contratto", LongParagraphs(words: 200)));

        Assert.All(PdfTestReader.Pages(pdf), page => Assert.Equal(string.Empty, page.Watermark));
    }

    [Fact]
    public void Render_AnyDocument_EmbedsOnlyTheBundledFonts()
    {
        var pdf = _sut.Render(new PdfDocumentContent(
            "Titolo",
            [new PdfHeading("Sezione"), new PdfParagraph("Testo normale"), new PdfParagraph("Testo in grassetto", Bold: true)])
        {
            Watermark = "BOZZA",
        });

        var fonts = PdfTestReader.FontNames(pdf);
        Assert.NotEmpty(fonts);
        Assert.All(fonts, font => Assert.StartsWith(EmbeddedFontResolver.FamilyName, font, StringComparison.Ordinal));
        var (descriptors, embedded) = PdfTestReader.FontEmbedding(pdf);
        Assert.True(descriptors > 0);
        Assert.Equal(descriptors, embedded);
    }

    [Theory]
    [InlineData("Arial", false)]
    [InlineData("Times New Roman", true)]
    [InlineData("Courier New", false)]
    [InlineData("A family no host has", true)]
    public void ResolveTypeface_AnyFamily_UsesTheEmbeddedFontNeverTheHost(string family, bool bold)
    {
        var info = EmbeddedFontResolver.Instance.ResolveTypeface(family, bold, isItalic: false);

        Assert.Equal(bold ? EmbeddedFontResolver.BoldFace : EmbeddedFontResolver.RegularFace, info.FaceName);
        var font = EmbeddedFontResolver.Instance.GetFont(info.FaceName);
        // TrueType outline font (sfnt version 1.0), read from the assembly resources.
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, font[..4]);
    }

    [Fact]
    public void Render_Table_RepeatsTheHeaderOnEveryPageAndKeepsEveryRow()
    {
        const int rowCount = 120;
        var table = new PdfTable(
            [new PdfTableColumn("Canale", 2), new PdfTableColumn("Lordo", 1, PdfCellAlignment.Right), new PdfTableColumn("Ritenuta", 1, PdfCellAlignment.Right)],
            Enumerable.Range(1, rowCount)
                .Select(i => (IReadOnlyList<string>)[$"riga{i:D3}", (i * 10.5m).ToString("N2", CultureInfo.GetCultureInfo("it-IT")), "21,00"])
                .ToList());

        var pdf = _sut.Render(new PdfDocumentContent("Report", [table]));

        var pages = PdfTestReader.Pages(pdf);
        Assert.True(pages.Count > 1);
        Assert.All(pages, page => Assert.Contains("Canale Lordo Ritenuta", page.Text, StringComparison.Ordinal));
        var rows = PdfTestReader.BodyWords(pdf).Where(w => w.StartsWith("riga", StringComparison.Ordinal)).ToList();
        Assert.Equal(Enumerable.Range(1, rowCount).Select(i => $"riga{i:D3}"), rows);
        Assert.Contains("1.260,00", PdfTestReader.Text(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WordWiderThanTheLine_IsSplitInsideTheMargins()
    {
        // MigraDoc breaks only at blanks: without splitting, these words would run past the right edge of the page.
        var longWord = string.Concat(Enumerable.Range(0, 30).Select(i => $"https://casazen.example/{i:D2}/"));
        const string iban = "IT60X0542811101000000123456";
        var content = new PdfDocumentContent("Link", [
            new PdfParagraph($"Vedi {longWord} fine"),
            new PdfTable(
                Enumerable.Range(1, 8).Select(i => new PdfTableColumn($"C{i}")).ToList(),
                [Enumerable.Range(1, 8).Select(_ => iban).ToList()]),
        ]);

        var pdf = _sut.Render(content);

        var rightMargin = (595.28 - 56.69) + 1;
        Assert.True(PdfTestReader.MaxTextRight(pdf) <= rightMargin, $"text reaches x={PdfTestReader.MaxTextRight(pdf):0.0}");
        var words = PdfTestReader.BodyWords(pdf).ToList();
        var from = words.IndexOf("Vedi") + 1;
        var to = words.IndexOf("fine");
        Assert.Equal(longWord, string.Concat(words.Skip(from).Take(to - from)));
        // The eight cells are rendered line by line across the row: each piece of the IBAN appears once per cell.
        var pieces = words.Skip(words.IndexOf("C8") + 1).ToList();
        var distinct = pieces.Distinct(StringComparer.Ordinal).ToList();
        Assert.True(distinct.Count > 1, "the IBAN should not fit a 60 pt column on one line");
        Assert.Equal(iban, string.Concat(distinct));
        Assert.All(distinct, piece => Assert.Equal(8, pieces.Count(p => p == piece)));
    }

    [Fact]
    public void Render_TableRowWithWrongCellCount_Throws()
    {
        var table = new PdfTable([new PdfTableColumn("A"), new PdfTableColumn("B")], [["solo una"]]);

        Assert.Throws<ArgumentException>(() => _sut.Render(new PdfDocumentContent("Report", [table])));
    }

    [Fact]
    public void FromPlainText_BlankLinesSplitParagraphs_SingleBreaksStay()
    {
        var blocks = PdfBlocks.FromPlainText("riga uno\r\nriga due\n\n\nterzo paragrafo\n");

        Assert.Equal(
            [new PdfParagraph("riga uno\nriga due"), new PdfParagraph("terzo paragrafo")],
            blocks);
    }

    /// <summary>Paragraphs of numbered words (w00001 …), about 6 characters each.</summary>
    private static IReadOnlyList<PdfBlock> LongParagraphs(int words) =>
        Enumerable.Range(1, words)
            .Chunk(90)
            .Select(chunk => (PdfBlock)new PdfParagraph(string.Join(' ', chunk.Select(Token))))
            .ToList();

    private static string Token(int i) => $"w{i:D5}";
}
