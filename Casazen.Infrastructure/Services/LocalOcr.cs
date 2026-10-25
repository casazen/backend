using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Casazen.Infrastructure.Services;

/// <summary>The OCR tools cannot run: not installed, the server is busy, or the read ran out of time.</summary>
internal sealed class OcrUnavailableException(Exception? inner = null) : Exception("OCR unavailable", inner);

/// <summary>
/// The plumbing the local OCR readers share (the energy certificate, <see cref="ApeOcrReader"/>, and the contracts,
/// <see cref="ContractTextReader"/>): two OCR jobs at a time on the host, a private temporary folder deleted on every way out, and the
/// tools (<c>pdftoppm</c>, <c>tesseract</c>) run without a shell, with their memory and CPU time limited by <c>prlimit</c>. Everything
/// happens on the server: no document and no text goes anywhere else.
/// </summary>
internal static class LocalOcr
{
    /// <summary>Two OCR runs at a time per API process, whoever asks: a long contract cannot starve the certificates, nor the other way round.</summary>
    private static readonly SemaphoreSlim Workers = new(2);

    internal const long MaxTextBytes = 1_000_000;

    /// <summary>
    /// Runs <paramref name="body"/> in a new private folder, holding one of the two OCR slots, within <paramref name="deadline"/> (the
    /// wait for a slot counts). The folder is deleted and the slot freed however it ends. Running out of time or a tool that cannot
    /// start is <see cref="OcrUnavailableException"/>; a cancellation by the caller is not.
    /// </summary>
    internal static async Task<T> WithWorkspaceAsync<T>(
        string prefix, TimeSpan deadline, Func<string, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(deadline);
        var token = linked.Token;
        var acquired = false;
        var directory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        try
        {
            await Workers.WaitAsync(token);
            acquired = true;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return await body(directory, token);
        }
        catch (Win32Exception ex) { throw new OcrUnavailableException(ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new OcrUnavailableException(ex); }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            finally { if (acquired) Workers.Release(); }
        }
    }

    internal static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaxTextBytes)
            throw new InvalidDataException("Unreadable OCR text.");
        return await File.ReadAllTextAsync(path, ct);
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (text.Length + count > MaxTextBytes)
                throw new InvalidDataException("OCR tool output exceeds limit.");
            text.Append(buffer, 0, count);
        }

        return text.ToString();
    }

    /// <summary>Runs a tool under <c>prlimit</c> (768 MiB of memory, 60 s of CPU), no shell, no user-supplied argument; its output is read up to <see cref="MaxTextBytes"/>.</summary>
    internal static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("prlimit") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var limit in new[] { "--as=805306368", "--cpu=60", "--", executable })
            start.ArgumentList.Add(limit);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        // Bound native decoder memory and threads. No shell, URL input or user-supplied arguments.
        start.Environment["OMP_THREAD_LIMIT"] = "1";
        using var process = Process.Start(start) ?? throw new OcrUnavailableException();
        var stdout = ReadLimitedAsync(process.StandardOutput, ct);
        var stderr = ReadLimitedAsync(process.StandardError, ct);
        try
        {
            await process.WaitForExitAsync(ct);
            var output = await stdout;
            var error = await stderr;
            if (error.Contains("Failed loading language", StringComparison.OrdinalIgnoreCase))
                throw new OcrUnavailableException();
            if (process.ExitCode is 126 or 127)
                throw new OcrUnavailableException();
            if (process.ExitCode != 0)
                throw new InvalidDataException("The decoder could not read the document.");
            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
