namespace Casazen.Infrastructure.Services;

internal static class ImageBytes
{
    public static async Task<byte[]> ReadAllAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining > maxBytes)
                throw new InvalidDataException("The picture is too large.");

            var exact = new byte[remaining];
            var filled = 0;
            while (filled < exact.Length)
            {
                var read = await stream.ReadAsync(exact.AsMemory(filled), cancellationToken);
                if (read == 0)
                    break;

                filled += read;
            }

            if (filled == exact.Length)
                return exact;

            var shorter = exact.AsSpan(0, filled).ToArray();
            Array.Clear(exact);
            return shorter;
        }

        var buffer = new byte[64 * 1024];
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    var bigger = new byte[Math.Min(buffer.Length * 2L, maxBytes + 1L)];
                    Buffer.BlockCopy(buffer, 0, bigger, 0, length);
                    Array.Clear(buffer);
                    buffer = bigger;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0)
                    break;

                length += read;
                if (length > maxBytes)
                    throw new InvalidDataException("The picture is too large.");
            }

            var result = new byte[length];
            Buffer.BlockCopy(buffer, 0, result, 0, length);
            return result;
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    public static void Zero(byte[]? bytes)
    {
        if (bytes is not null)
            Array.Clear(bytes);
    }

    public static void Zero(float[]? values)
    {
        if (values is not null)
            Array.Clear(values);
    }
}
