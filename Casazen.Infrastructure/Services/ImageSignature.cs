namespace Casazen.Infrastructure.Services;

/// <summary>
/// Recognizes JPEG, PNG and WebP images from their first bytes. The extension and the content type of an upload are
/// declared by the client; the signature is what the file really is, so a text or an executable renamed to
/// <c>.jpg</c> is refused before it reaches the public bucket (PC-04).
/// </summary>
internal static class ImageSignature
{
    /// <summary>Bytes needed to tell the supported formats apart.</summary>
    public const int HeaderLength = 12;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] WebP = "WEBP"u8.ToArray();

    /// <summary>The content type (<c>image/jpeg</c>, <c>image/png</c>, <c>image/webp</c>) of the image, or null for anything else.</summary>
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

    /// <summary>Reads the header of <paramref name="content"/> (at most <see cref="HeaderLength"/> bytes) and detects its type.</summary>
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
