using System.Globalization;
using Casazen.Core.Services;
using UglyToad.PdfPig;

namespace Casazen.Infrastructure.Services;

public sealed class ContractTextReader : IContractTextReader
{
    private const int MaxPages = 60;

    private const int MaxOcrPages = 20;

    private const long MaxBytes = 20 * 1024 * 1024;

    private const int MinCharactersOfAPage = 60;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(240);

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

        var progress = new Progress(carried);
        try
        {
            return await LocalOcr.WithWorkspaceAsync("casazen-contract", Deadline, (directory, token) => OcrAsync(directory, pdf, progress, token), cancellationToken);
        }
        catch (OcrUnavailableException ex)
        {
            var partial = progress.Texts;
            if (partial.Any(HasText))
                return Build(partial, progress.OcrPages, truncated: true);
            throw new ContractTextUnavailableException(ex);
        }
    }

    public Task<ContractText?> ReadDigitalAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf.Length is 0 || pdf.Length > MaxBytes)
            throw new InvalidDataException("Not a PDF document.");

        var carried = CarriedTexts(pdf, out _);
        return Task.FromResult<ContractText?>(carried is not null && carried.Any(HasText) ? Build(carried, 0, truncated: false) : null);
    }

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
