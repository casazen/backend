using System.IO.Compression;
using System.Text;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F07/US01/T02 (NF-111): the strict content scan of the files of the owner portal and the name a download carries. What the scan cannot read it refuses (an
/// encrypted PDF, a stream written with a filter that turns it into text), a stream is read from where it starts (an «endstream» inside its data does not cut it
/// short), and a download never carries the extension that came with the upload.
/// </summary>
public class OwnerFileScannerTests
{
    private static OwnerFileVerdict Scan(byte[] content) => new OwnerFileContentScanner().Scan(content);

    private static OwnerFileVerdict Scan(string text) => Scan(Encoding.Latin1.GetBytes(text));

    [Theory]
    // Encrypted: a reader opens it without a password, and everything the scan would read is ciphertext.
    [InlineData("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R /Encrypt 5 0 R >>\n%%EOF")]
    [InlineData("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R /Enc#72ypt 5 0 R >>\n%%EOF")]
    // An object stream written as text (hex, base 85, LZW, run length) hides the names the scan looks for: refused, with the long and the short names.
    [InlineData("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter [/ASCIIHexDecode /FlateDecode] >>\nstream\n789c4b2c2e48>\nendstream\nendobj\n%%EOF")]
    [InlineData("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter [/ASCII85Decode /FlateDecode] >>\nstream\n<~GatU4~>\nendstream\nendobj\n%%EOF")]
    [InlineData("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter /LZWDecode >>\nstream\n\u0080\u000b\u0060\nendstream\nendobj\n%%EOF")]
    [InlineData("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter /RunLengthDecode >>\nstream\n\u0002abc\u0080\nendstream\nendobj\n%%EOF")]
    [InlineData("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter [/AHx /Fl] >>\nstream\n789c>\nendstream\nendobj\n%%EOF")]
    [InlineData("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter [/A85 /Fl] >>\nstream\n<~GatU4~>\nendstream\nendobj\n%%EOF")]
    [InlineData("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter [/A#53CII85Decode /FlateDecode] >>\nstream\n<~GatU4~>\nendstream\nendobj\n%%EOF")]
    public void Pdf_ThatTheScanCannotRead_IsRefused_AsActiveContent(string pdf)
    {
        var verdict = Scan(pdf);

        Assert.Equal((false, OwnerFileRules.ActiveContent), (verdict.Clean, verdict.RejectionCode));
    }

    [Fact]
    public void Pdf_WithTheFiltersOfAScan_IsAccepted_AsBefore()
    {
        // A scanned page: a JPEG image (DCTDecode) and a content stream written with FlateDecode, no name the scan refuses.
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /XObject /Subtype /Image /Filter /DCTDecode /Length 6 >>\nstream\n")
            .Concat(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 })
            .Concat(Encoding.Latin1.GetBytes("\nendstream\nendobj\n2 0 obj << /Filter /FlateDecode >>\nstream\n"))
            .Concat(Deflate("q 100 0 0 100 0 0 cm /Im0 Do Q", CompressionLevel.Optimal))
            .Concat(Encoding.Latin1.GetBytes("\nendstream\nendobj\n%%EOF"))
            .ToArray();

        var verdict = Scan(pdf);

        Assert.Equal((true, OwnerFileRules.Pdf, ".pdf"), (verdict.Clean, verdict.ContentType, verdict.Extension));
    }

    [Fact]
    public void Pdf_AStreamIsReadFromWhereItStarts_AnEndstreamInsideItsDataDoesNotCutItShort()
    {
        // The zlib data of the object stream starts with a stored block that holds the word «endstream», and goes on with a compressed block that holds a script
        // (no name in clear anywhere in the file). Cut at the first «endstream» the data would not inflate and the script would never be read.
        var data = ZlibStoredThenCompressed("endstream\nstream\n", "2 0 obj << /S /JavaScript /JS (x) >> endobj");
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter /FlateDecode >>\nstream\n")
            .Concat(data)
            .Concat(Encoding.Latin1.GetBytes("\nendstream\nendobj\n%%EOF"))
            .ToArray();
        Assert.Contains("endstream", Encoding.Latin1.GetString(data), StringComparison.Ordinal);
        Assert.DoesNotContain("JavaScript", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        Assert.Equal(OwnerFileRules.ActiveContent, Scan(pdf).RejectionCode);
    }

    [Fact]
    public void Pdf_WithMoreStreamsThanTheScanReads_IsRefused_WhetherOrNotAnEndstreamFollows()
    {
        // Thousands of «stream» and not one «endstream», then a megabyte: the scan counts every stream wherever it starts, and does not search the megabyte for the
        // end of each (that search was one for each stream, a cost of streams times bytes).
        var many = string.Concat(Enumerable.Repeat("stream\n", 6000));
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n" + many).Concat(new byte[1024 * 1024]).ToArray();

        Assert.Equal(OwnerFileRules.ActiveContent, Scan(pdf).RejectionCode);
    }

    [Theory]
    [InlineData("a.pdf.bat", ".pdf", "a_pdf.pdf")]
    [InlineData("fattura.pdf.exe", ".pdf", "fattura_pdf.pdf")]
    [InlineData("..\\..\\Windows\\evil.hta", ".pdf", "evil.pdf")]
    [InlineData("C:/dir/foto.JPG", ".jpg", "foto.jpg")]
    [InlineData("invoice\u202Efdp.exe", ".pdf", "invoicefdp.pdf")]
    [InlineData("a\r\nb\0c.pdf", ".pdf", "abc.pdf")]
    [InlineData("a;b\"c<d>.pdf", ".pdf", "a_b_c_d.pdf")]
    [InlineData("Visura Verdi Maria.pdf", ".pdf", "Visura Verdi Maria.pdf")]
    [InlineData("   ", ".png", "file.png")]
    [InlineData(null, ".pdf", "file.pdf")]
    [InlineData(".bashrc", ".pdf", "file.pdf")]
    [InlineData("scan", ".png", "scan.png")]
    public void SafeFileName_KeepsTheStemAndOnlyTheExtensionOfTheTypeTheScanRecognised(string? sent, string extension, string expected)
    {
        var name = OwnerFileRules.SafeFileName(sent, extension, 200);

        Assert.Equal(expected, name);
        // Safe once, safe again: the controller makes the stored name safe a second time.
        Assert.Equal(expected, OwnerFileRules.SafeFileName(name, extension, 200));
        // One dot, the one of the extension of the type.
        Assert.Single(name, '.');
    }

    [Fact]
    public void SafeFileName_ACutNameKeepsItsExtension()
    {
        var name = OwnerFileRules.SafeFileName(new string('x', 300) + ".pdf", ".pdf", 200);

        Assert.Equal(200, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OwnerFileRules.Pdf, ".pdf")]
    [InlineData(OwnerFileRules.Jpeg, ".jpg")]
    [InlineData(OwnerFileRules.Png, ".png")]
    [InlineData("text/html", "")]
    [InlineData(null, "")]
    public void ExtensionOf_IsTheOneOfTheThreeTypesTheScanAccepts(string? contentType, string expected) =>
        Assert.Equal(expected, OwnerFileRules.ExtensionOf(contentType));

    private static byte[] Deflate(string text, CompressionLevel level)
    {
        using var target = new MemoryStream();
        using (var zlib = new ZLibStream(target, level, leaveOpen: true))
            zlib.Write(Encoding.Latin1.GetBytes(text));
        return target.ToArray();
    }

    /// <summary>
    /// One zlib stream made by hand: a stored block that holds <paramref name="stored"/> as it is (so its bytes can say «endstream»), then the compressed block of
    /// <paramref name="compressed"/> (so its words are not in clear anywhere in the file), then the Adler-32 of everything.
    /// </summary>
    private static byte[] ZlibStoredThenCompressed(string stored, string compressed)
    {
        var storedBytes = Encoding.Latin1.GetBytes(stored);
        var compressedBytes = Encoding.Latin1.GetBytes(compressed);
        using var raw = new MemoryStream();
        using (var deflate = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(compressedBytes);

        int length = storedBytes.Length;
        var data = new List<byte> { 0x78, 0x01, 0x00, (byte)(length & 0xFF), (byte)(length >> 8), (byte)(~length & 0xFF), (byte)((~length >> 8) & 0xFF) };
        data.AddRange(storedBytes);
        data.AddRange(raw.ToArray());

        uint a = 1;
        uint b = 0;
        foreach (var value in storedBytes.Concat(compressedBytes))
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var adler = (b << 16) | a;
        data.AddRange([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);
        return data.ToArray();
    }
}
