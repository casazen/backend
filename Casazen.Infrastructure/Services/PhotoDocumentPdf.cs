using Casazen.Core.Exceptions;
using Microsoft.AspNetCore.Http;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Casazen.Infrastructure.Services;

/// <summary>Single-image clients also persist a PDF; the browser combines and rotates multi-page selections before upload.</summary>
internal static class PhotoDocumentPdf
{
    internal static async Task<IFormFile> NormalizeAsync(IFormFile file)
    {
        if (Path.GetExtension(file.FileName).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png")) return file;
        using var input = new MemoryStream();
        await file.CopyToAsync(input);
        if (input.Length is 0 or > 10 * 1024 * 1024 || ImageSignature.Detect(input.GetBuffer().AsSpan(0, (int)Math.Min(input.Length, ImageSignature.HeaderLength))) is not ("image/jpeg" or "image/png"))
            throw new DomainRuleException("invalid_document_file", "DocumentFileInvalid");
        try
        {
            input.Position = 0;
            using var image = XImage.FromStream(input);
            if (image.PixelWidth > 15000 || image.PixelHeight > 15000 || (long)image.PixelWidth * image.PixelHeight > 40_000_000)
                throw new DomainRuleException("invalid_document_file", "DocumentFileInvalid");
            using var document = new PdfDocument();
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(image.PixelWidth * 0.3);
            page.Height = XUnit.FromPoint(image.PixelHeight * 0.3);
            using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            using var output = new MemoryStream();
            document.Save(output, closeStream: false);
            var bytes = output.ToArray();
            return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", Path.GetFileNameWithoutExtension(file.FileName) + ".pdf")
            { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
        }
        catch (DomainRuleException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.IOException)
        {
            throw new DomainRuleException("invalid_document_file", "DocumentFileInvalid");
        }
    }
}
