using System.Globalization;
using System.IO.Compression;
using System.Text;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Documents;

internal static class ScannedPdf
{
    private const int Dpi = 200;

    private const double PageWidthPoints = 595.28;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static readonly Lazy<Task<bool>> ToolsInstalled = new(ProbeToolsAsync);

    private static async Task<bool> ProbeToolsAsync()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await LocalOcr.RunAsync("tesseract", ["--list-langs"], limit.Token);
            await LocalOcr.RunAsync("pdftoppm", ["-v"], limit.Token);
            return true;
        }
        catch (Exception ex) when (ex is OcrUnavailableException or InvalidDataException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static async Task<byte[]?> FromAsync(byte[] digitalPdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digitalPdf);

        if (!await ToolsInstalled.Value)
            return null;

        try
        {
            return await LocalOcr.WithWorkspaceAsync(
                "casazen-scan",
                Deadline,
                async (directory, token) =>
                {
                    var input = Path.Combine(directory, "digital.pdf");
                    await File.WriteAllBytesAsync(input, digitalPdf, token);
                    await LocalOcr.RunAsync("pdftoppm", ["-gray", "-r", Dpi.ToString(CultureInfo.InvariantCulture), input, Path.Combine(directory, "page")], token);

                    var pages = new List<GrayImage>();
                    foreach (var file in Directory.GetFiles(directory, "page-*.pgm").OrderBy(PageNumber))
                        pages.Add(GrayImage.Read(await File.ReadAllBytesAsync(file, token)));

                    return pages.Count == 0 ? null : Build(pages);
                },
                cancellationToken);
        }
        catch (OcrUnavailableException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int PageNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(name[(name.LastIndexOf('-') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue;
    }

    public static byte[] Build(IReadOnlyList<GrayImage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
            throw new ArgumentException("A scan needs at least one page.", nameof(pages));

        static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        using var pdf = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        void StartObject(int id)
        {
            offsets.Add(pdf.Position);
            Write($"{id} 0 obj\n");
        }

        Write("%PDF-1.4\n");
        StartObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        StartObject(2);
        Write($"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages.Count).Select(i => $"{3 + (3 * i)} 0 R"))}] /Count {pages.Count} >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var image = pages[i];
            var pageId = 3 + (3 * i);
            var pageHeight = Math.Round(PageWidthPoints * image.Height / image.Width, 2);
            var content = $"q {Number(PageWidthPoints)} 0 0 {Number(pageHeight)} 0 0 cm /Im0 Do Q";

            byte[] pixels;
            using (var compressed = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                    zlib.Write(image.Pixels);
                pixels = compressed.ToArray();
            }

            StartObject(pageId);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Number(PageWidthPoints)} {Number(pageHeight)}] /Resources << /XObject << /Im0 {pageId + 1} 0 R >> >> /Contents {pageId + 2} 0 R >>\nendobj\n");
            StartObject(pageId + 1);
            Write($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {pixels.Length} >>\nstream\n");
            pdf.Write(pixels);
            Write("\nendstream\nendobj\n");
            StartObject(pageId + 2);
            Write($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        var xref = pdf.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }
}
