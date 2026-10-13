using System.IO.Compression;
using System.Text;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Extracts literal PDF strings from uncompressed and FlateDecode content streams.
/// Enough to recognize an official APE without a third-party PDF library.
/// </summary>
internal static class PdfLiteralTextExtractor
{
    internal const int MaxFlateStreams = 128;
    internal const int MaxDecodedBytesPerStream = 2 * 1024 * 1024;
    internal const int MaxTotalDecodedBytes = 8 * 1024 * 1024;

    private const int CopyBufferSize = 8192;

    private static readonly byte[] StreamToken = "stream"u8.ToArray();
    private static readonly byte[] EndStreamToken = "endstream"u8.ToArray();
    private static readonly byte[] FlateToken = "/FlateDecode"u8.ToArray();

    public static string Extract(byte[] pdf)
    {
        var sb = new StringBuilder();
        AppendLiterals(pdf, sb);

        foreach (var decoded in DecodeFlateStreams(pdf))
            AppendLiterals(decoded, sb);

        return sb.ToString();
    }

    private static IEnumerable<byte[]> DecodeFlateStreams(byte[] pdf)
    {
        var offset = 0;
        var streamsSeen = 0;
        var totalDecoded = 0;
        while (streamsSeen < MaxFlateStreams && totalDecoded < MaxTotalDecodedBytes)
        {
            var streamAt = IndexOf(pdf, StreamToken, offset);
            if (streamAt < 0)
                yield break;

            var dictStart = LastIndexOf(pdf, (byte)'<', streamAt);
            var isFlate = dictStart >= 0
                && IndexOf(pdf, FlateToken, dictStart) is var filterAt
                && filterAt >= 0
                && filterAt < streamAt;

            var dataStart = SkipStreamNewline(pdf, streamAt + StreamToken.Length);
            var endAt = IndexOf(pdf, EndStreamToken, dataStart);
            if (endAt < 0)
                yield break;

            if (isFlate && endAt > dataStart)
            {
                streamsSeen++;
                var remaining = MaxTotalDecodedBytes - totalDecoded;
                var cap = Math.Min(MaxDecodedBytesPerStream, remaining);
                if (cap <= 0)
                    yield break;

                var payload = pdf[dataStart..endAt];
                var status = TryInflate(payload, cap, out var decoded);
                if (status == InflateStatus.Oversize)
                    totalDecoded += cap;
                else if (status == InflateStatus.Success)
                {
                    totalDecoded += decoded.Length;
                    yield return decoded;
                }
            }

            offset = endAt + EndStreamToken.Length;
        }
    }

    private static InflateStatus TryInflate(byte[] payload, int maxDecodedBytes, out byte[] decoded)
    {
        decoded = [];
        if (payload.Length == 0 || maxDecodedBytes <= 0)
            return InflateStatus.Empty;

        var status = TryInflateWith(payload, useZlib: true, maxDecodedBytes, out decoded);
        if (status is InflateStatus.Success or InflateStatus.Oversize)
            return status;

        return TryInflateWith(payload, useZlib: false, maxDecodedBytes, out decoded);
    }

    private static InflateStatus TryInflateWith(byte[] payload, bool useZlib, int maxDecodedBytes, out byte[] decoded)
    {
        decoded = [];
        try
        {
            using var input = new MemoryStream(payload);
            Stream decompressor = useZlib
                ? new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true)
                : new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
            using (decompressor)
            {
                using var output = new MemoryStream();
                var status = CopyBounded(decompressor, output, maxDecodedBytes);
                if (status != InflateStatus.Success)
                    return status;

                decoded = output.ToArray();
                return decoded.Length > 0 ? InflateStatus.Success : InflateStatus.Empty;
            }
        }
        catch (InvalidDataException)
        {
            return InflateStatus.Empty;
        }
    }

    private static InflateStatus CopyBounded(Stream source, Stream dest, int maxBytes)
    {
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (dest.Length + read > maxBytes)
                return InflateStatus.Oversize;

            dest.Write(buffer, 0, read);
        }

        return dest.Length > 0 ? InflateStatus.Success : InflateStatus.Empty;
    }

    private enum InflateStatus
    {
        Empty,
        Success,
        Oversize
    }

    private static void AppendLiterals(byte[] data, StringBuilder sb)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'(')
                continue;
            if (i > 0 && data[i - 1] == (byte)'\\')
                continue;

            i++;
            while (i < data.Length)
            {
                var b = data[i];
                if (b == (byte)')')
                    break;
                if (b == (byte)'\\' && i + 1 < data.Length)
                {
                    i++;
                    sb.Append(Unescape(data[i]));
                }
                else if (b is >= 32 and <= 126)
                {
                    sb.Append((char)b);
                }
                else if (b is 10 or 13 or 9)
                {
                    sb.Append(' ');
                }

                i++;
            }

            sb.Append(' ');
        }
    }

    private static char Unescape(byte b) => b switch
    {
        (byte)'n' => '\n',
        (byte)'r' => '\r',
        (byte)'t' => '\t',
        (byte)'(' => '(',
        (byte)')' => ')',
        (byte)'\\' => '\\',
        _ => (char)b
    };

    private static int SkipStreamNewline(byte[] pdf, int i)
    {
        if (i < pdf.Length && pdf[i] == (byte)'\r')
            i++;
        if (i < pdf.Length && pdf[i] == (byte)'\n')
            i++;
        return i;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        var max = haystack.Length - needle.Length;
        for (var i = start; i <= max; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    private static int LastIndexOf(byte[] haystack, byte value, int startExclusive)
    {
        for (var i = Math.Min(startExclusive, haystack.Length) - 1; i >= 0; i--)
        {
            if (haystack[i] == value)
                return i;
        }

        return -1;
    }
}
