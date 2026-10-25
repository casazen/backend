using System.Globalization;
using Casazen.Core.Services;
using UglyToad.PdfPig;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Reads the text of a contract PDF (NL-DOCS) on the server, page by page. A page that carries text (a digital PDF: read in process by
/// PdfPig, no tool needed) is taken as it is; a page that does not (a scan, the PDF of the photos of a contract) is rendered in gray and
/// read by Tesseract, Italian and English, with the same limits as the energy certificate: two OCR runs at a time on the host
/// (<see cref="LocalOcr"/>), a deadline of 110 seconds, at most <see cref="MaxOcrPages"/> pages read by OCR. What cannot be read in time
/// is left out and said (<see cref="ContractText.Truncated"/>); a PDF that cannot be opened is read by OCR whole, or refused. No text is
/// ever logged.
/// </summary>
public sealed class ContractTextReader : IContractTextReader
{
    /// <summary>More pages than this is not a contract with its annexes.</summary>
    private const int MaxPages = 60;

    /// <summary>Pages read by OCR: as many as the certificate of the energy performance (the time is the limit, not the memory).</summary>
    private const int MaxOcrPages = 12;

    private const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>A page with fewer letters and digits than this carries no text of its own (a scan may hold a few stray characters).</summary>
    private const int MinCharactersOfAPage = 60;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(110);

    public async Task<ContractText> ReadAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf.Length is 0 || pdf.Length > MaxBytes)
            throw new InvalidDataException("Not a contract PDF.");

        var carried = CarriedTexts(pdf, out var count);
        if (count > MaxPages)
            throw new InvalidDataException("Too many pages for a contract.");
        if (carried is not null && carried.All(HasText))
            return Build(carried, 0, truncated: false);

        // Some pages (or, when the PDF cannot be opened here, all of them) need OCR.
        var progress = new Progress(carried);
        try
        {
            return await LocalOcr.WithWorkspaceAsync("casazen-contract", Deadline, (directory, token) => OcrAsync(directory, pdf, progress, token), cancellationToken);
        }
        catch (OcrUnavailableException ex)
        {
            // What the pages carry, and what OCR read before it stopped, is still the contract: it is unavailable only if there is nothing.
            var partial = progress.Texts;
            if (partial.Any(HasText))
                return Build(partial, progress.OcrPages, truncated: true);
            throw new ContractTextUnavailableException(ex);
        }
    }

    /// <summary>The text of each page as the PDF carries it, or <c>null</c> when PdfPig cannot open the file (damaged, protected, an odd generator).</summary>
    private static List<string>? CarriedTexts(byte[] pdf, out int count)
    {
        count = 0;
        try
        {
            using var document = PdfDocument.Open(pdf);
            count = document.NumberOfPages;
            if (count is < 1 or > MaxPages)
                return null;

            var texts = new List<string>(count);
            foreach (var page in document.GetPages())
                texts.Add(string.Join(' ', page.GetWords().Select(word => word.Text)));
            return texts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool HasText(string text) => text.Count(char.IsLetterOrDigit) >= MinCharactersOfAPage;

    private static ContractText Build(IReadOnlyList<string> pages, int ocrPages, bool truncated) =>
        new(string.Join("\n\f\n", pages), pages.Count, ocrPages, truncated);

    /// <summary>The pages as far as they are read: the text the PDF carries for each, replaced by the OCR's as the pages without text are read.</summary>
    private sealed class Progress(List<string>? carried)
    {
        public List<string> Texts { get; set; } = carried ?? [];

        public int OcrPages { get; set; }
    }

    private static async Task<ContractText> OcrAsync(string directory, byte[] pdf, Progress progress, CancellationToken token)
    {
        var input = Path.Combine(directory, "input.pdf");
        await File.WriteAllBytesAsync(input, pdf, token);

        var total = progress.Texts.Count;
        if (total == 0)
        {
            var info = await LocalOcr.RunAsync("pdfinfo", [input], token);
            var line = info.Split('\n').FirstOrDefault(l => l.StartsWith("Pages:", StringComparison.Ordinal));
            if (line is null || !int.TryParse(line[6..].Trim(), CultureInfo.InvariantCulture, out total) || total is < 1 or > MaxPages)
                throw new InvalidDataException("Not a contract PDF.");
            progress.Texts = Enumerable.Repeat(string.Empty, total).ToList();
        }

        var truncated = false;
        for (var page = 1; page <= total; page++)
        {
            if (HasText(progress.Texts[page - 1]))
                continue;

            if (progress.OcrPages >= MaxOcrPages)
            {
                truncated = true;
                continue;
            }

            var number = page.ToString(CultureInfo.InvariantCulture);
            var image = Path.Combine(directory, "page");
            await LocalOcr.RunAsync("pdftoppm", ["-f", number, "-l", number, "-singlefile", "-gray", "-scale-to", "2400", input, image], token);
            var output = Path.Combine(directory, "ocr");
            await LocalOcr.RunAsync("tesseract", [image + ".pgm", output, "-l", "ita+eng", "--psm", "3", "-c", "thresholding_method=2", "txt"], token);
            progress.Texts[page - 1] = $"{progress.Texts[page - 1]}\n{await LocalOcr.ReadTextAsync(output + ".txt", token)}".Trim();
            progress.OcrPages++;
            File.Delete(image + ".pgm");
        }

        return Build(progress.Texts, progress.OcrPages, truncated);
    }
}
