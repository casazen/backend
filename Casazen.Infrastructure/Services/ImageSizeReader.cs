using System.Buffers.Binary;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Reads the size in pixels of a JPEG, PNG or WebP image from its header, without decoding it (DB-11a): what the property page needs to
/// reserve the room of a photo before it arrives. A JPEG is read as a browser draws it, so a photo a phone took on its side (EXIF
/// orientation 5 to 8) gives its width and height swapped. Anything that cannot be read is <c>null</c>: the size is only a hint, never a
/// reason to refuse an upload (the type was already checked by <see cref="ImageSignature"/>).
/// </summary>
internal static class ImageSizeReader
{
    private const int MaxExifBytes = 65_535;

    /// <summary>The size of the image in <paramref name="content"/>, read from its start; <c>null</c> when it cannot be told.</summary>
    public static async Task<(int Width, int Height)?> ReadAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var head = new byte[30];
        var read = await ReadAtMostAsync(content, head, cancellationToken);
        var type = ImageSignature.Detect(head.AsSpan(0, read));
        return type switch
        {
            "image/png" => Png(head.AsSpan(0, read)),
            "image/webp" => WebP(head.AsSpan(0, read)),
            "image/jpeg" => await JpegAsync(content, head.AsMemory(0, read), cancellationToken),
            _ => null,
        };
    }

    // ─── PNG: the IHDR chunk right after the signature ──────────────────────────────────────────────

    private static (int, int)? Png(ReadOnlySpan<byte> head)
    {
        if (head.Length < 24 || !head.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;

        return Valid(BinaryPrimitives.ReadUInt32BigEndian(head.Slice(16, 4)), BinaryPrimitives.ReadUInt32BigEndian(head.Slice(20, 4)));
    }

    // ─── WebP: lossy (VP8 ), lossless (VP8L) or extended (VP8X) ─────────────────────────────────────

    private static (int, int)? WebP(ReadOnlySpan<byte> head)
    {
        if (head.Length < 25)
            return null;

        var kind = head.Slice(12, 4);
        if (kind.SequenceEqual("VP8 "u8) && head.Length >= 30 && head[23] == 0x9D && head[24] == 0x01 && head[25] == 0x2A)
        {
            return Valid(
                (uint)(BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(26, 2)) & 0x3FFF),
                (uint)(BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(28, 2)) & 0x3FFF));
        }

        if (kind.SequenceEqual("VP8L"u8) && head[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(21, 4));
            return Valid((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }

        if (kind.SequenceEqual("VP8X"u8) && head.Length >= 30)
        {
            var width = (uint)(head[24] | (head[25] << 8) | (head[26] << 16)) + 1;
            var height = (uint)(head[27] | (head[28] << 8) | (head[29] << 16)) + 1;
            return Valid(width, height);
        }

        return null;
    }

    // ─── JPEG: walk the segments to the start of frame, noting the EXIF orientation on the way ──────

    private static async Task<(int, int)?> JpegAsync(Stream content, ReadOnlyMemory<byte> alreadyRead, CancellationToken cancellationToken)
    {
        // The first bytes were read to tell the type; the walk continues from the stream, so they are put back in front of it.
        var reader = new PrefixedReader(content, alreadyRead);
        var orientation = 1;

        // FF D8 (start of image).
        if (!await reader.SkipAsync(2, cancellationToken))
            return null;

        var one = new byte[1];
        var pair = new byte[2];
        var guard = 0;
        while (guard++ < 4096)
        {
            // The next marker: FF, then the code (FF again is padding).
            if (!await reader.ReadExactAsync(one, cancellationToken))
                return null;
            if (one[0] != 0xFF)
                continue;

            byte marker;
            do
            {
                if (!await reader.ReadExactAsync(one, cancellationToken))
                    return null;
                marker = one[0];
            }
            while (marker == 0xFF);

            // Markers with no length: stuffed zero, start of image, restart markers, TEM.
            if (marker is 0x00 or 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7)
                continue;

            // End of image, or the start of the scan: no frame header was found.
            if (marker is 0xD9 or 0xDA)
                return null;

            if (!await reader.ReadExactAsync(pair, cancellationToken))
                return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(pair) - 2;
            if (length < 0)
                return null;

            // Start of frame (baseline, extended, progressive, lossless...): precision, height, width.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                var frame = new byte[5];
                if (length < frame.Length || !await reader.ReadExactAsync(frame, cancellationToken))
                    return null;

                var height = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3, 2));
                return orientation is >= 5 and <= 8 ? Valid(height, width) : Valid(width, height);
            }

            if (marker == 0xE1 && length is > 14 and <= MaxExifBytes)
            {
                var payload = new byte[length];
                if (!await reader.ReadExactAsync(payload, cancellationToken))
                    return null;

                orientation = ExifOrientation(payload) ?? orientation;
                continue;
            }

            if (!await reader.SkipAsync(length, cancellationToken))
                return null;
        }

        return null;
    }

    /// <summary>The orientation (1 to 8) of the EXIF block of an APP1 segment; <c>null</c> when it has none.</summary>
    private static int? ExifOrientation(ReadOnlySpan<byte> app1)
    {
        // "Exif" and two zero bytes, then a TIFF header: byte order (II or MM), 42, the offset of the first directory.
        if (app1.Length < 14 || !app1[..6].SequenceEqual("Exif\0\0"u8))
            return null;

        var tiff = app1[6..];
        var little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!little && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M'))
            return null;

        if (ReadU16(tiff, 2, little) != 42)
            return null;

        var directory = ReadU32(tiff, 4, little);
        if (directory > tiff.Length - 2)
            return null;

        int entries = ReadU16(tiff, (int)directory, little);
        for (var i = 0; i < entries; i++)
        {
            var at = (int)directory + 2 + (i * 12);
            if (at + 12 > tiff.Length)
                return null;

            // Tag 0x0112 (orientation), type 3 (short), one value stored in the first two bytes of the value field.
            if (ReadU16(tiff, at, little) == 0x0112 && ReadU16(tiff, at + 2, little) == 3 && ReadU32(tiff, at + 4, little) == 1)
            {
                var value = ReadU16(tiff, at + 8, little);
                return value is >= 1 and <= 8 ? value : null;
            }
        }

        return null;
    }

    // ─── Small helpers ──────────────────────────────────────────────────────────────────────────────

    private static ushort ReadU16(ReadOnlySpan<byte> bytes, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at, 2));

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(at, 4));

    private static (int, int)? Valid(uint width, uint height) =>
        width is > 0 and <= 65_535 && height is > 0 and <= 65_535 ? ((int)width, (int)height) : null;

    private static async Task<int> ReadAtMostAsync(Stream content, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await content.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
                break;

            read += count;
        }

        return read;
    }

    /// <summary>A stream read from the start after its first bytes were already taken off it.</summary>
    private sealed class PrefixedReader(Stream rest, ReadOnlyMemory<byte> prefix)
    {
        private int _used;

        public async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            var filled = 0;
            while (filled < buffer.Length)
            {
                if (_used < prefix.Length)
                {
                    var take = Math.Min(prefix.Length - _used, buffer.Length - filled);
                    prefix.Slice(_used, take).CopyTo(buffer.AsMemory(filled));
                    _used += take;
                    filled += take;
                    continue;
                }

                var count = await rest.ReadAsync(buffer.AsMemory(filled), cancellationToken);
                if (count == 0)
                    return false;

                filled += count;
            }

            return true;
        }

        public async Task<bool> SkipAsync(int count, CancellationToken cancellationToken)
        {
            var scratch = new byte[Math.Min(Math.Max(count, 1), 4096)];
            while (count > 0)
            {
                var chunk = Math.Min(count, scratch.Length);
                var target = chunk == scratch.Length ? scratch : new byte[chunk];
                if (!await ReadExactAsync(target, cancellationToken))
                    return false;

                count -= chunk;
            }

            return true;
        }
    }
}
