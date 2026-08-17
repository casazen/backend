using System.IO.Compression;
using System.Text;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PdfLiteralTextExtractorTests
{
    [Fact]
    public void Extract_FlateEncodedLiteral_ReturnsText()
    {
        var pdf = FlatePdf("ATTESTATO DI PRESTAZIONE ENERGETICA");

        var text = PdfLiteralTextExtractor.Extract(pdf);

        Assert.Contains("ATTESTATO DI PRESTAZIONE ENERGETICA", text);
    }

    [Fact]
    public void Extract_FlateZipBomb_DoesNotMaterializeDecodedPayload()
    {
        var pdf = FlatePdfFromRaw(new byte[5 * 1024 * 1024]);

        var text = PdfLiteralTextExtractor.Extract(pdf);

        Assert.True(pdf.Length < 100_000);
        Assert.True(text.Length < 1_000);
    }

    [Fact]
    public void Extract_StopsAfterMaxFlateStreams()
    {
        using var pdf = new MemoryStream();
        pdf.Write("%PDF-1.4\n"u8);
        for (var i = 0; i < PdfLiteralTextExtractor.MaxFlateStreams; i++)
            WriteFlateObject(pdf, i + 1, "JUNK");
        WriteFlateObject(pdf, PdfLiteralTextExtractor.MaxFlateStreams + 1, "MARKER-AFTER-CAP");
        pdf.Write("%%EOF"u8);

        var text = PdfLiteralTextExtractor.Extract(pdf.ToArray());

        Assert.DoesNotContain("MARKER-AFTER-CAP", text);
        Assert.Contains("JUNK", text);
    }

    private static byte[] FlatePdf(string text) =>
        FlatePdfFromRaw(Encoding.ASCII.GetBytes($"BT ({text}) Tj ET"));

    private static void WriteFlateObject(Stream pdf, int objectNumber, string text)
    {
        var payload = Compress(Encoding.ASCII.GetBytes($"BT ({text}) Tj ET"));
        var header = Encoding.ASCII.GetBytes(
            $"{objectNumber} 0 obj << /Length {payload.Length} /Filter /FlateDecode >> stream\n");
        pdf.Write(header);
        pdf.Write(payload);
        pdf.Write("\nendstream endobj\n"u8);
    }

    private static byte[] FlatePdfFromRaw(byte[] content)
    {
        var payload = Compress(content);
        using var pdf = new MemoryStream();
        var header = Encoding.ASCII.GetBytes(
            $"%PDF-1.4\n1 0 obj << /Length {payload.Length} /Filter /FlateDecode >> stream\n");
        pdf.Write(header);
        pdf.Write(payload);
        pdf.Write("\nendstream endobj\n%%EOF"u8);
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
