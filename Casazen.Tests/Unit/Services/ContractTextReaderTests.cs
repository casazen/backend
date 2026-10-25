using Casazen.Infrastructure.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>NL-DOCS: a digital contract is read where it is, without OCR; nothing is not a contract.</summary>
public sealed class ContractTextReaderTests
{
    [Fact]
    public async Task ADigitalPdf_IsReadFromItsOwnText_WithoutAnyOcr()
    {
        var pdf = BuildPdf(
            "TRA Il Sig. Mario Rossi, nato a Roma il 01/01/1980, residente in Roma, Via dei Mille n. 5,",
            "codice fiscale RSSMRA80A01H501U, di seguito denominato Locatore, e la Sig.ra Giulia Verdi,",
            "Il canone mensile di locazione e di Euro 800,00 da pagarsi entro il giorno 5 di ogni mese.");

        var text = await new ContractTextReader().ReadAsync(pdf);

        Assert.Equal(1, text.Pages);
        Assert.Equal(0, text.PagesOcr);
        Assert.False(text.Truncated);
        Assert.Contains("RSSMRA80A01H501U", text.Text);
        Assert.Contains("800,00", text.Text);
    }

    [Fact]
    public async Task NoBytes_IsNotAContract()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new ContractTextReader().ReadAsync([]));
    }

    // PdfPig's builder uses the standard 14 PDF fonts, so it needs no system fonts.
    private static byte[] BuildPdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var y = 780d;
        foreach (var line in lines)
        {
            page.AddText(line, 9, new PdfPoint(40, y), font);
            y -= 18;
        }

        return builder.Build();
    }
}
