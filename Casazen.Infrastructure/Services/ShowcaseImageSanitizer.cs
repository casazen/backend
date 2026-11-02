using System.Buffers.Binary;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Writes an image of a showcase again <b>without its metadata</b> (SP-13b), in plain managed code and without touching the picture: the
/// pixels are not decoded and not compressed again, the file is read segment by segment (JPEG), chunk by chunk (PNG, WebP) and only what a
/// browser needs to draw it is copied. What does not get through: the EXIF block with the GPS position, the camera and the time, the thumbnail
/// inside it, XMP, the IPTC and Photoshop blocks, comments, text chunks, the "extra" images of some phones, and every byte after the end of the
/// image (a file can be a valid picture followed by anything, even a page or a script).
/// </summary>
/// <remarks>
/// <para><b>What stays.</b> The header and the tables the picture is made of, the color profile (ICC) and the few chunks that say how colors
/// are read (gamma, sRGB, transparency, palette), so the picture looks the same. <b>The orientation</b> of a JPEG is the one piece of
/// metadata kept: a photo taken on its side is rewritten with a minimal EXIF block that holds the orientation and nothing else, so a browser
/// draws it upright (the single tag is not personal data).</para>
/// <para><b>Strict on purpose.</b> A file whose structure is not that of a whole, plain image of its type answers <c>null</c> (the caller
/// refuses it): a segment that runs past the end, a PNG with no <c>IEND</c>, a WebP that is animated or has no picture. A JPEG cut off before its
/// end-of-image marker is accepted and closed, since a browser draws it. Nothing is ever stored that this class did not rewrite.</para>
/// </remarks>
internal static class ShowcaseImageSanitizer
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // The chunks of a PNG that are kept: the critical ones and those that say how the colors are read. Everything else (text, EXIF, time, the
    // chunks of an animation) is dropped; an animated PNG becomes its still image, which every decoder draws.
    private static readonly byte[][] KeptPngChunks =
    [
        "IHDR"u8.ToArray(), "PLTE"u8.ToArray(), "IDAT"u8.ToArray(), "IEND"u8.ToArray(),
        "tRNS"u8.ToArray(), "gAMA"u8.ToArray(), "cHRM"u8.ToArray(), "sRGB"u8.ToArray(), "iCCP"u8.ToArray(), "sBIT"u8.ToArray(), "pHYs"u8.ToArray(),
    ];

    /// <summary>
    /// The image in <paramref name="source"/> (of <paramref name="contentType"/>, <c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>, already
    /// told from its first bytes) written again without its metadata; <c>null</c> when it is not a whole, still image of that type.
    /// </summary>
    public static byte[]? Clean(byte[] source, string contentType)
    {
        ArgumentNullException.ThrowIfNull(source);
        return contentType switch
        {
            "image/jpeg" => Jpeg(source),
            "image/png" => Png(source),
            "image/webp" => WebP(source),
            _ => null,
        };
    }

    // ─── JPEG: a list of segments, then the entropy-coded data of each scan ────────────────────────

    private static byte[]? Jpeg(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return null;

        var kept = new List<ReadOnlyMemory<byte>>();
        var jfifFirst = false;
        var orientation = 1;
        var sawFrame = false;
        var sawScan = false;
        var pos = 2;

        while (pos < data.Length)
        {
            // The next marker: one or more FF (fill bytes are allowed), then its code.
            if (data[pos] != 0xFF)
                return null;

            while (pos < data.Length && data[pos] == 0xFF)
                pos++;

            if (pos >= data.Length)
                return null;

            var marker = data[pos++];
            var segmentStart = pos - 2;

            // The end of the image: nothing after it is kept.
            if (marker == 0xD9)
                break;

            // Markers that stand alone cannot be here: a stuffed zero, TEM, a restart marker, a second start of image.
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8))
                return null;

            if (pos + 2 > data.Length)
                return null;

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
            if (length < 2 || pos + length > data.Length)
                return null;

            // The payload is what follows the length; the whole segment is the marker, the length and the payload.
            var payload = data.AsMemory(pos + 2, length - 2);
            var segment = data.AsMemory(segmentStart, 2 + length);
            pos += length;

            switch (marker)
            {
                case 0xE1:
                    // EXIF or XMP: never kept. The orientation of the photo is the only thing taken from the EXIF block.
                    if (payload.Span.StartsWith("Exif\0\0"u8))
                        orientation = ImageSizeReader.ExifOrientation(payload.Span) ?? orientation;
                    break;

                case 0xE0 when payload.Span.StartsWith("JFIF\0"u8):
                    // The JFIF header says how the picture is read; its thumbnail (a smaller copy, often of the picture before it was cropped) is not kept.
                    if (payload.Length < 12)
                        return null;

                    if (kept.Count == 0)
                        jfifFirst = true;

                    kept.Add(JfifWithoutThumbnail(payload.Span));
                    break;

                case 0xE2 when payload.Span.StartsWith("ICC_PROFILE\0"u8):
                case 0xEE when payload.Span.StartsWith("Adobe"u8):
                    // The color profile, and the Adobe marker that says how the colors of some files (CMYK) are read.
                    kept.Add(segment);
                    break;

                case >= 0xE0 and <= 0xEF:
                case 0xFE:
                    // Any other application segment (XMP, IPTC, Photoshop, the extra pictures of a phone, a JFXX thumbnail) and the comments.
                    break;

                case 0xDA:
                    // Start of scan: its header, then the entropy-coded data up to the next marker.
                    if (!sawFrame)
                        return null;

                    sawScan = true;
                    kept.Add(segment);

                    var scanEnd = EndOfScan(data, pos);
                    if (scanEnd > pos)
                        kept.Add(data.AsMemory(pos, scanEnd - pos));

                    pos = scanEnd;
                    break;

                default:
                    // A frame header, a table, a restart interval...: what the picture is made of.
                    if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                    {
                        if (length < 8)
                            return null;

                        sawFrame = true;
                    }

                    kept.Add(segment);
                    break;
            }
        }

        if (!sawFrame || !sawScan)
            return null;

        // A photo taken on its side keeps its orientation, as a block of one tag, right after the JFIF header when there is one.
        var exif = orientation is >= 2 and <= 8 ? OrientationSegment(orientation) : null;
        var exifAt = jfifFirst ? 1 : 0;

        using var output = new MemoryStream(data.Length);
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);
        for (var i = 0; i <= kept.Count; i++)
        {
            if (i == exifAt && exif is not null)
                output.Write(exif);

            if (i < kept.Count)
                output.Write(kept[i].Span);
        }

        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        return output.ToArray();
    }

    /// <summary>
    /// Where the entropy-coded data that starts at <paramref name="start"/> ends: at the FF of the next marker (a stuffed FF 00, a restart
    /// marker and the fill bytes before a marker are part of the data), or at the end of the file when it was cut off.
    /// </summary>
    private static int EndOfScan(byte[] data, int start)
    {
        var end = start;
        while (end + 1 < data.Length)
        {
            if (data[end] != 0xFF)
            {
                end++;
                continue;
            }

            var next = data[end + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
            {
                // A stuffed FF, or a restart marker: still the data of this scan.
                end += 2;
                continue;
            }

            if (next == 0xFF)
            {
                // A fill byte before a marker.
                end++;
                continue;
            }

            return end;
        }

        // A file cut off in the data of its last scan runs to its end; it is closed by the caller.
        return data.Length;
    }

    /// <summary>The JFIF header written again with a thumbnail of 0 by 0: the same version, units and densities.</summary>
    private static byte[] JfifWithoutThumbnail(ReadOnlySpan<byte> payload)
    {
        // Marker, length (16), "JFIF\0", the version (2), the units (1), the densities (2 + 2), the size of the thumbnail (0, 0).
        var segment = new byte[2 + 2 + 14];
        segment[0] = 0xFF;
        segment[1] = 0xE0;
        segment[2] = 0x00;
        segment[3] = 0x10;
        payload[..12].CopyTo(segment.AsSpan(4));
        return segment;
    }

    /// <summary>An EXIF block that holds one tag, the orientation (2 to 8), in big-endian TIFF: 36 bytes with the marker and the length.</summary>
    private static byte[] OrientationSegment(int orientation) =>
    [
        0xFF, 0xE1, 0x00, 0x22,                          // APP1, length 34
        0x45, 0x78, 0x69, 0x66, 0x00, 0x00,              // "Exif" and two zero bytes
        0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,  // TIFF header: big-endian, 42, the first directory at offset 8
        0x00, 0x01,                                      // one entry
        0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,  // tag 0x0112 (orientation), type SHORT, one value
        0x00, (byte)orientation, 0x00, 0x00,             // the value, in the first two bytes of the field
        0x00, 0x00, 0x00, 0x00,                          // no next directory
    ];

    // ─── PNG: signature, then length / type / data / CRC chunks up to IEND ─────────────────────────

    private static byte[]? Png(byte[] data)
    {
        if (data.Length < 8 + 12 || !data.AsSpan(0, 8).SequenceEqual(PngSignature))
            return null;

        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 8);

        var pos = 8;
        var first = true;
        var sawData = false;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            if (length > int.MaxValue || pos + 12L + length > data.Length)
                return null;

            var type = data.AsSpan(pos + 4, 4);
            if (!IsChunkName(type))
                return null;

            // The first chunk is the header of the image, 13 bytes.
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                    return null;

                first = false;
            }

            var total = 12 + (int)length;
            if (KeepsPngChunk(type))
                output.Write(data, pos, total);

            if (type.SequenceEqual("IDAT"u8))
                sawData = true;

            pos += total;

            // The end of the image: whatever follows is not part of it and is not kept.
            if (type.SequenceEqual("IEND"u8))
                return sawData ? output.ToArray() : null;
        }

        // No IEND: the file is cut short.
        return null;
    }

    private static bool IsChunkName(ReadOnlySpan<byte> type)
    {
        foreach (var letter in type)
        {
            if (letter is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')))
                return false;
        }

        return true;
    }

    private static bool KeepsPngChunk(ReadOnlySpan<byte> type)
    {
        foreach (var kept in KeptPngChunks)
        {
            if (type.SequenceEqual(kept))
                return true;
        }

        return false;
    }

    // ─── WebP: a RIFF container of chunks ──────────────────────────────────────────────────────────

    private static byte[]? WebP(byte[] data)
    {
        if (data.Length < 20 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return null;

        // The size of the container: what follows it (a file can carry anything after its end) is not kept.
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        if (riffSize < 12 || riffSize + 8L > data.Length)
            return null;

        var end = (int)riffSize + 8;
        var chunks = new List<byte[]>();
        var images = 0;
        var pos = 12;
        while (pos + 8 <= end)
        {
            var id = data.AsSpan(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            if (pos + 8L + size > end)
                return null;

            var payload = data.AsSpan(pos + 8, (int)size);

            // A chunk is padded to an even size.
            pos += 8 + (int)size + (int)(size & 1);

            if (id.SequenceEqual("VP8X"u8))
            {
                // The extended format: the flags say which optional chunks follow. An animation is refused; the flags of the EXIF and the XMP
                // go with their chunks (0x08 and 0x04).
                if (size < 10 || (payload[0] & 0x02) != 0)
                    return null;

                var header = payload.ToArray();
                header[0] &= 0xF3;
                chunks.Add(Chunk("VP8X"u8, header));
            }
            else if (id.SequenceEqual("VP8 "u8) || id.SequenceEqual("VP8L"u8))
            {
                images++;
                chunks.Add(Chunk(id, payload));
            }
            else if (id.SequenceEqual("ALPH"u8) || id.SequenceEqual("ICCP"u8))
            {
                chunks.Add(Chunk(id, payload));
            }
            else if (id.SequenceEqual("ANIM"u8) || id.SequenceEqual("ANMF"u8))
            {
                return null;
            }

            // EXIF, XMP and every chunk this code does not know are not kept.
        }

        // One still picture.
        if (images != 1)
            return null;

        var body = 0;
        foreach (var chunk in chunks)
            body += chunk.Length;

        var output = new byte[12 + body];
        "RIFF"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), (uint)(4 + body));
        "WEBP"u8.CopyTo(output.AsSpan(8));

        var at = 12;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(output, at);
            at += chunk.Length;
        }

        return output;
    }

    /// <summary>A chunk of a RIFF container: its name, its size (without the padding), the data, and a zero byte when the size is odd.</summary>
    private static byte[] Chunk(ReadOnlySpan<byte> id, ReadOnlySpan<byte> payload)
    {
        var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
        id.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(chunk.AsSpan(8));
        return chunk;
    }
}
