using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.CfSimulator;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// A fact that needs the tools of the real OCR (<c>pdftoppm</c>, <c>tesseract</c>, <c>prlimit</c>). Without them on a local run it is reported as
/// skipped with the reason; on CI, which installs them (<c>.github/workflows/ci-cd.yml</c>, «Install local document OCR tools»), it always runs, so
/// a missing tool can never hide a regression of the pipeline.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class OcrToolsFactAttribute : FactAttribute
{
    private static readonly string[] Tools = ["pdftoppm", "tesseract", "prlimit"];

    public OcrToolsFactAttribute()
    {
        if (Tools.FirstOrDefault(tool => !OnPath(tool)) is { } missing && !IsContinuousIntegration())
            Skip = $"{missing} is not installed here: the real OCR path is proved on CI (poppler-utils, tesseract-ocr-ita and -eng, util-linux).";
    }

    private static bool IsContinuousIntegration() =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool OnPath(string tool)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, tool)) || File.Exists(Path.Combine(directory, tool + ".exe")))
                    return true;
            }
            catch (ArgumentException)
            {
                // A folder of the PATH that is not a path: it holds nothing.
            }
        }

        return false;
    }
}

/// <summary>
/// NL-DOCS-FU7: the way of a <b>scanned</b> contract, with the real tools. CF-SIM proved the reading of a PDF that carries its own text; nothing had
/// run the other road, a page that is only a picture: rendered in gray by <c>pdftoppm</c> and read by <c>tesseract</c> (Italian and English). Here the
/// sample contract of the simulated module is rendered by the PDF renderer of the application, turned into a picture with <c>pdftoppm</c> and put in a
/// PDF that has no text (a page with one image, as a scanner or the photos of a contract make it), and the real reader has to read it by OCR.
/// </summary>
public sealed class ContractScanOcrTests
{
    [OcrToolsFact]
    public async Task AScannedContract_WithNoTextOfItsOwn_IsReadByTheRealOcr()
    {
        var house = new CfSimulatorHouse("Via Sparano 12", "Bari", "70121", "Bari");
        var digital = new MigraDocPdfDocumentRenderer().Render(CfSimulatorDocuments.Contract(house, new DateOnly(2026, 10, 25)));
        var scan = await ScanOfAsync(digital);

        var text = await new ContractTextReader().ReadAsync(scan);

        // The page was read by OCR (it carries no text), and what came out is the contract: the title, the rent and the deposit. Letters and digits only,
        // in capitals: the punctuation and the spaces are where an OCR may differ.
        Assert.Equal(1, text.Pages);
        Assert.Equal(1, text.PagesOcr);
        var read = new string(text.Text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        Assert.Contains("LOCAZIONE", read, StringComparison.Ordinal);
        Assert.Contains("CONCORDATO", read, StringComparison.Ordinal);
        Assert.Contains("65000", read, StringComparison.Ordinal);
        Assert.Contains("130000", read, StringComparison.Ordinal);
    }

    /// <summary>The page of <paramref name="digitalPdf"/> as a picture in gray, in a PDF that holds nothing else: what a scan is.</summary>
    private static async Task<byte[]> ScanOfAsync(byte[] digitalPdf)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"casazen-scan-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "digital.pdf");
            await File.WriteAllBytesAsync(input, digitalPdf);

            var start = new ProcessStartInfo("pdftoppm") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var argument in new[] { "-gray", "-r", "200", "-singlefile", input, Path.Combine(directory, "page") })
                start.ArgumentList.Add(argument);
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("pdftoppm did not start."))
            {
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, error);
            }

            var image = GrayImage.Read(await File.ReadAllBytesAsync(Path.Combine(directory, "page.pgm")));
            return ImageOnlyPdf(image);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A PDF of one A4-wide page whose only content is the image, 8-bit gray, deflated: no text, no font.</summary>
    private static byte[] ImageOnlyPdf(GrayImage image)
    {
        byte[] pixels;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(image.Pixels);
            pixels = compressed.ToArray();
        }

        string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        var pageWidth = 595.28;
        var pageHeight = Math.Round(pageWidth * image.Height / image.Width, 2);
        var content = $"q {Number(pageWidth)} 0 0 {Number(pageHeight)} 0 0 cm /Im0 Do Q";

        using var pdf = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));

        Write("%PDF-1.4\n");
        offsets.Add(pdf.Position);
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets.Add(pdf.Position);
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets.Add(pdf.Position);
        Write($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Number(pageWidth)} {Number(pageHeight)}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        offsets.Add(pdf.Position);
        Write($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {pixels.Length} >>\nstream\n");
        pdf.Write(pixels);
        Write("\nendstream\nendobj\n");
        offsets.Add(pdf.Position);
        Write($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        var xref = pdf.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }
}
