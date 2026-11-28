namespace Casazen.Infrastructure.Services;

internal static class ImageSignature
{
    public const int HeaderLength = 12;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] WebP = "WEBP"u8.ToArray();

    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(Jpeg))
            return "image/jpeg";
        if (header.StartsWith(Png))
            return "image/png";
        if (header.Length >= HeaderLength && header[..4].SequenceEqual(Riff) && header.Slice(8, 4).SequenceEqual(WebP))
            return "image/webp";
        return null;
    }

    public static async Task<string?> DetectAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        var read = 0;
        while (read < header.Length)
        {
            var count = await content.ReadAsync(header.AsMemory(read), cancellationToken);
            if (count == 0)
                break;
            read += count;
        }

        return Detect(header.AsSpan(0, read));
    }
}
