using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>Local OCR: no certificate data leaves the API host. Temporary inputs are deleted on every exit path.</summary>
internal static class ApeOcrReader
{
    private static readonly SemaphoreSlim Workers = new(2);
    private const int MaxPages = 12;
    private const long MaxTextBytes = 1_000_000;

    internal static async Task<string> ReadAsync(byte[] bytes, bool isPdf, Func<string, ApeInspectionResult> inspectExtractedText, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var acquired = false;
        var directory = Path.Combine(Path.GetTempPath(), $"casazen-ape-{Guid.NewGuid():N}");
        try
        {
            await Workers.WaitAsync(token);
            acquired = true;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var input = Path.Combine(directory, isPdf ? "input.pdf" : "input.image");
            await File.WriteAllBytesAsync(input, bytes, token);
            var text = new StringBuilder();
            var pages = 1;
            if (isPdf)
            {
                var info = await RunAsync("pdfinfo", [input], token);
                var pageLine = info.Split('\n').FirstOrDefault(line => line.StartsWith("Pages:", StringComparison.Ordinal));
                if (pageLine is null || !int.TryParse(pageLine[6..].Trim(), out pages) || pages is < 1 or > MaxPages)
                    throw new InvalidDataException("APE must contain between 1 and 12 pages.");
                var extracted = Path.Combine(directory, "embedded.txt");
                await RunAsync("pdftotext", ["-layout", input, extracted], token);
                text.Append(await ReadTextAsync(extracted, token));
                var inspection = inspectExtractedText(text.ToString());
                if (inspection.Status is ApeInspectionStatus.Valid or ApeInspectionStatus.Expired)
                    return text.ToString();
            }
            for (var page = 1; page <= pages; page++)
            {
                var image = input;
                if (isPdf)
                {
                    var prefix = Path.Combine(directory, "page");
                    await RunAsync("pdftoppm", ["-f", page.ToString(System.Globalization.CultureInfo.InvariantCulture), "-l", page.ToString(System.Globalization.CultureInfo.InvariantCulture), "-singlefile", "-scale-to", "2400", "-png", input, prefix], token);
                    image = prefix + ".png";
                }
                var output = Path.Combine(directory, "ocr");
                await RunAsync("tesseract", [image, output, "-l", "ita+eng", "--psm", "3"], token);
                text.AppendLine(await ReadTextAsync(output + ".txt", token));
                if (text.Length > MaxTextBytes) throw new InvalidDataException("APE text exceeds limit.");
            }
            return text.ToString();
        }
        catch (Win32Exception ex) { throw new ApeOcrUnavailableException(ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new ApeOcrUnavailableException(ex); }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            finally { if (acquired) Workers.Release(); }
        }
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaxTextBytes) throw new InvalidDataException("Unreadable APE text.");
        return await File.ReadAllTextAsync(path, ct);
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (text.Length + count > MaxTextBytes) throw new InvalidDataException("APE tool output exceeds limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("prlimit") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var limit in new[] { "--as=805306368", "--cpu=60", "--", executable }) start.ArgumentList.Add(limit);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Bound native decoder memory and threads. No shell, URL input or user-supplied arguments.
        start.Environment["OMP_THREAD_LIMIT"] = "1";
        using var process = Process.Start(start) ?? throw new ApeOcrUnavailableException();
        var stdout = ReadLimitedAsync(process.StandardOutput, ct);
        var stderr = ReadLimitedAsync(process.StandardError, ct);
        try
        {
            await process.WaitForExitAsync(ct);
            var output = await stdout;
            var error = await stderr;
            if (error.Contains("Failed loading language", StringComparison.OrdinalIgnoreCase)) throw new ApeOcrUnavailableException();
            if (process.ExitCode is 126 or 127) throw new ApeOcrUnavailableException();
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("APE decoder could not read the document.");
            }
            return output;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}

internal sealed class ApeOcrUnavailableException(Exception? inner = null) : Exception("APE OCR unavailable", inner);
