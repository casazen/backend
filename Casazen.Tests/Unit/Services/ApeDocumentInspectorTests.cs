using System.IO.Compression;
using System.Text;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ApeDocumentInspectorTests
{
    private readonly ApeDocumentInspector _sut = new();

    [Fact]
    public void InspectExtractedText_OfficialApeWording_IsValid()
    {
        var text = """
            ATTESTATO DI PRESTAZIONE ENERGETICA
            Classe energetica G
            EPgl,nren 180 kWh/m2 anno
            SIAPE codice identificativo
            Certificatore energetico
            """;

        var result = _sut.InspectExtractedText(text);

        Assert.True(result.IsValid);
        Assert.Equal(ApeInspectionStatus.Valid, result.Status);
    }

    [Fact]
    public void InspectExtractedText_GenericLeasePdf_IsContentMismatch()
    {
        var result = _sut.InspectExtractedText("Contratto di locazione a canone concordato. Canone mensile 750 euro.");

        Assert.False(result.IsValid);
        Assert.Equal(ApeInspectionStatus.ContentMismatch, result.Status);
    }

    [Fact]
    public void InspectExtractedText_Empty_IsContentMismatch()
    {
        var result = _sut.InspectExtractedText("   ");

        Assert.Equal(ApeInspectionStatus.ContentMismatch, result.Status);
    }

    [Fact]
    public void Inspect_OfficialApePdf_IsValid()
    {
        var pdf = FiscalPdfWriter.Write(
            "ATTESTATO DI PRESTAZIONE ENERGETICA",
            "Classe energetica D. EPgl,nren 95. SIAPE. Certificatore energetico. D.Lgs. 192/2005.");

        using var stream = new MemoryStream(pdf);
        var result = _sut.Inspect(stream);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Inspect_NonApePdf_IsContentMismatch()
    {
        var pdf = FiscalPdfWriter.Write("Contratto di locazione", "Canone mensile e deposito cauzionale.");

        using var stream = new MemoryStream(pdf);
        var result = _sut.Inspect(stream);

        Assert.False(result.IsValid);
        Assert.Equal(ApeInspectionStatus.ContentMismatch, result.Status);
    }

    [Fact]
    public void Inspect_RandomBytes_IsNotAPdf()
    {
        using var stream = new MemoryStream("this is not a pdf"u8.ToArray());
        var result = _sut.Inspect(stream);

        Assert.Equal(ApeInspectionStatus.NotAPdf, result.Status);
    }

    [Fact]
    public void Inspect_FlateEncodedApePdf_IsValid()
    {
        var pdf = FlatePdf("ATTESTATO DI PRESTAZIONE ENERGETICA Classe energetica A4 EPgl SIAPE");

        using var stream = new MemoryStream(pdf);
        var result = _sut.Inspect(stream);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Inspect_FlateZipBomb_IsContentMismatchWithoutExpandingUnbounded()
    {
        var pdf = FlatePdfFromRaw(new byte[5 * 1024 * 1024]);

        using var stream = new MemoryStream(pdf);
        var result = _sut.Inspect(stream);

        Assert.Equal(ApeInspectionStatus.ContentMismatch, result.Status);
        Assert.True(pdf.Length < 100_000);
    }

    [Fact]
    public void Inspect_ManyFlateStreams_FindsApeInEarlyStream()
    {
        var pdf = MultiFlatePdf(
            PdfLiteralTextExtractor.MaxFlateStreams + 50,
            "ATTESTATO DI PRESTAZIONE ENERGETICA Classe energetica A4 EPgl SIAPE");

        using var stream = new MemoryStream(pdf);
        var result = _sut.Inspect(stream);

        Assert.True(result.IsValid);
    }

    private static byte[] FlatePdf(string text)
    {
        var content = Encoding.ASCII.GetBytes($"BT ({text}) Tj ET");
        return FlatePdfFromRaw(content);
    }

    private static byte[] MultiFlatePdf(int streamCount, string text)
    {
        using var pdf = new MemoryStream();
        pdf.Write("%PDF-1.4\n"u8);
        for (var i = 0; i < streamCount; i++)
        {
            var payload = Compress(Encoding.ASCII.GetBytes($"BT ({text}) Tj ET"));
            var header = Encoding.ASCII.GetBytes(
                $"{i + 1} 0 obj << /Length {payload.Length} /Filter /FlateDecode >> stream\n");
            pdf.Write(header);
            pdf.Write(payload);
            pdf.Write("\nendstream endobj\n"u8);
        }

        pdf.Write("%%EOF"u8);
        return pdf.ToArray();
    }

    private static byte[] FlatePdfFromRaw(byte[] content)
    {
        var payload = Compress(content);

        using var pdf = new MemoryStream();
        var header = Encoding.ASCII.GetBytes(
            $"%PDF-1.4\n1 0 obj << /Length {payload.Length} /Filter /FlateDecode >> stream\n");
        var footer = Encoding.ASCII.GetBytes("\nendstream endobj\n%%EOF");
        pdf.Write(header);
        pdf.Write(payload);
        pdf.Write(footer);
        return pdf.ToArray();
    }

    private static byte[] Compress(byte[] content)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(content);
        return compressed.ToArray();
    }
}
