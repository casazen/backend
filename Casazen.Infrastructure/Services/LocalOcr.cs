using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Casazen.Infrastructure.Services;

internal sealed class OcrUnavailableException(Exception? inner = null) : Exception("OCR unavailable", inner);

internal static class LocalOcr
{
    private static readonly SemaphoreSlim Workers = new(2);

    internal const long MaxTextBytes = 1_000_000;

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

    internal static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("prlimit") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var limit in new[] { "--as=805306368", "--cpu=60", "--", executable })
            start.ArgumentList.Add(limit);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
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
