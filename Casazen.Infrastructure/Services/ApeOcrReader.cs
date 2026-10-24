using System.Diagnostics;
using System.Text;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Local OCR: no certificate data leaves the API host. Temporary inputs are deleted on every exit path.
/// </summary>
/// <remarks>
/// A page of a scan or a photo is rendered in gray and read three ways: as it is (automatic layout, Sauvola thresholding for the
/// uneven light of a photo), as its negative (white text on the dark bands: title, header with code and expiry, box of the class)
/// and, around the labels found by those two, zone by zone (<see cref="ApeAnchors"/>). The texts are joined: the checks and the
/// reading of the data look at all of them. The two extra readings stop after <see cref="ExtraPassesUntil"/>, so a long document
/// still ends within the deadline. The tools, the temporary folder and the two slots are <see cref="LocalOcr"/>'s, shared with the
/// reader of the contracts.
/// </remarks>
internal static class ApeOcrReader
{
    private const int MaxPages = 12;
    private const long MaxPageImageBytes = 30 * 1024 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(110);
    private static readonly TimeSpan ExtraPassesUntil = TimeSpan.FromSeconds(60);

    internal static async Task<string> ReadAsync(byte[] bytes, bool isPdf, Func<string, ApeInspectionResult> inspectExtractedText, CancellationToken ct)
    {
        try
        {
            return await LocalOcr.WithWorkspaceAsync(
                "casazen-ape", Deadline, (directory, token) => ReadInAsync(directory, bytes, isPdf, inspectExtractedText, token), ct);
        }
        catch (OcrUnavailableException ex)
        {
            throw new ApeOcrUnavailableException(ex);
        }
    }

    private static async Task<string> ReadInAsync(
        string directory, byte[] bytes, bool isPdf, Func<string, ApeInspectionResult> inspectExtractedText, CancellationToken token)
    {
        var input = Path.Combine(directory, isPdf ? "input.pdf" : "input.image");
        await File.WriteAllBytesAsync(input, bytes, token);
        var text = new StringBuilder();
        var pages = 1;
        if (isPdf)
        {
            var info = await LocalOcr.RunAsync("pdfinfo", [input], token);
            var pageLine = info.Split('\n').FirstOrDefault(line => line.StartsWith("Pages:", StringComparison.Ordinal));
            if (pageLine is null || !int.TryParse(pageLine[6..].Trim(), out pages) || pages is < 1 or > MaxPages)
                throw new InvalidDataException("APE must contain between 1 and 12 pages.");
            var extracted = Path.Combine(directory, "embedded.txt");
            await LocalOcr.RunAsync("pdftotext", ["-layout", input, extracted], token);
            text.Append(await LocalOcr.ReadTextAsync(extracted, token));
            var inspection = inspectExtractedText(text.ToString());
            if (inspection.Status is ApeInspectionStatus.Valid or ApeInspectionStatus.Expired)
                return text.ToString();
        }
        var elapsed = Stopwatch.StartNew();
        for (var page = 1; page <= pages; page++)
        {
            if (isPdf)
            {
                var prefix = Path.Combine(directory, "page");
                var number = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await LocalOcr.RunAsync("pdftoppm", ["-f", number, "-l", number, "-singlefile", "-gray", "-scale-to", "2400", input, prefix], token);
                await ReadPageAsync(directory, prefix + ".pgm", text, extraPasses: elapsed.Elapsed < ExtraPassesUntil, token);
            }
            else
            {
                // A single photo sent as such by an older client: read as it is.
                var output = Path.Combine(directory, "ocr");
                await LocalOcr.RunAsync("tesseract", [input, output, "-l", "ita+eng", "--psm", "3"], token);
                text.AppendLine(await LocalOcr.ReadTextAsync(output + ".txt", token));
            }
            if (text.Length > LocalOcr.MaxTextBytes) throw new InvalidDataException("APE text exceeds limit.");
        }
        return text.ToString();
    }

    /// <summary>One page in gray: as it is, as its negative, then zone by zone around the labels the two readings found.</summary>
    private static async Task ReadPageAsync(string directory, string pagePath, StringBuilder text, bool extraPasses, CancellationToken token)
    {
        var plain = Path.Combine(directory, "plain");
        await LocalOcr.RunAsync("tesseract", [pagePath, plain, "-l", "ita+eng", "--psm", "3", "-c", "thresholding_method=2", "txt", "tsv"], token);
        text.AppendLine(await LocalOcr.ReadTextAsync(plain + ".txt", token));
        if (!extraPasses)
            return;

        if (!File.Exists(pagePath) || new FileInfo(pagePath).Length > MaxPageImageBytes)
            throw new InvalidDataException("Unreadable APE page image.");
        var page = GrayImage.Read(await File.ReadAllBytesAsync(pagePath, token));
        var negativePath = Path.Combine(directory, "negative.pgm");
        await File.WriteAllBytesAsync(negativePath, page.Negated().ToPgm(), token);
        var negative = Path.Combine(directory, "negative");
        await LocalOcr.RunAsync("tesseract", [negativePath, negative, "-l", "ita+eng", "--psm", "11", "txt", "tsv"], token);
        text.AppendLine(await LocalOcr.ReadTextAsync(negative + ".txt", token));

        var words = OcrWord.ParseTsv(await LocalOcr.ReadTextAsync(plain + ".tsv", token))
            .Concat(OcrWord.ParseTsv(await LocalOcr.ReadTextAsync(negative + ".tsv", token)));
        var index = 0;
        foreach (var (left, top, right, bottom) in ApeAnchors.Boxes(words))
        {
            if (page.Crop(left, top, right, bottom) is not { } zone)
                continue;
            var zonePath = Path.Combine(directory, $"zone{index++}.pgm");
            await File.WriteAllBytesAsync(zonePath, zone.ToPgm(), token);
            var output = Path.Combine(directory, "zone");
            await LocalOcr.RunAsync("tesseract", [zonePath, output, "-l", "ita+eng", "--psm", "6"], token);
            text.AppendLine(await LocalOcr.ReadTextAsync(output + ".txt", token));
        }
    }
}

internal sealed class ApeOcrUnavailableException(Exception? inner = null) : Exception("APE OCR unavailable", inner);
