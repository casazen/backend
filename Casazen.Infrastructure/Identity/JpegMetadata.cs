namespace Casazen.Infrastructure.Identity;

/// <summary>
/// Takes the metadata out of a JPEG before it is kept for a reviewer (E73/F01/US02/T01, NF-195, ADR-013-R20): every application segment (<c>APP0</c> to <c>APP15</c>:
/// JFIF, EXIF, XMP, ICC profiles, the data of the device and of the place) and every comment (<c>COM</c>) goes; the tables, the frame, the scans and their entropy-coded
/// data stay byte for byte, so the picture is the one that was judged. Whatever follows the end of the image (the trailers some phones append) goes too. A file whose
/// structure cannot be followed to its end is not stripped (<see cref="TryStrip"/> answers false) and the caller keeps something else, never the file as it came.
/// In memory only: no file, no library; the copy it returns is the caller's to zero.
/// </summary>
internal static class JpegMetadata
{
    private const byte Marker = 0xFF;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;
    private const byte Comment = 0xFE;
    private const byte FirstApplicationSegment = 0xE0;
    private const byte LastApplicationSegment = 0xEF;

    /// <summary>
    /// The JPEG <paramref name="jpeg"/> without its application segments and comments, in <paramref name="stripped"/>; false (and an empty array) for bytes that are not a
    /// complete JPEG: no start of image, a segment that runs past the end, no end of image.
    /// </summary>
    public static bool TryStrip(ReadOnlySpan<byte> jpeg, out byte[] stripped)
    {
        stripped = [];
        if (jpeg.Length < 4 || jpeg[0] != Marker || jpeg[1] != StartOfImage)
            return false;

        var output = new byte[jpeg.Length];
        var written = 0;
        var complete = false;
        try
        {
            output[written++] = Marker;
            output[written++] = StartOfImage;
            var position = 2;
            while (position < jpeg.Length)
            {
                if (jpeg[position] != Marker)
                    return false;

                // Fill bytes (a run of 0xFF before a marker) carry nothing.
                while (position < jpeg.Length && jpeg[position] == Marker)
                    position++;

                if (position >= jpeg.Length)
                    return false;

                var marker = jpeg[position++];
                if (marker == EndOfImage)
                {
                    output[written++] = Marker;
                    output[written++] = EndOfImage;
                    complete = true;
                    break;
                }

                // Markers without a length: a second start of image, the restart markers and the temporary one.
                if (marker == StartOfImage || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                {
                    output[written++] = Marker;
                    output[written++] = marker;
                    continue;
                }

                if (position + 2 > jpeg.Length)
                    return false;

                var length = (jpeg[position] << 8) | jpeg[position + 1];
                if (length < 2 || position + length > jpeg.Length)
                    return false;

                var segment = jpeg.Slice(position, length);
                position += length;
                if (marker is >= FirstApplicationSegment and <= LastApplicationSegment || marker == Comment)
                    continue;

                output[written++] = Marker;
                output[written++] = marker;
                segment.CopyTo(output.AsSpan(written));
                written += length;

                if (marker == StartOfScan)
                {
                    // The entropy-coded data of the scan, up to the next marker: a 0xFF followed by 0x00 is a byte of the data, a restart marker belongs to the scan.
                    var start = position;
                    while (position < jpeg.Length)
                    {
                        if (jpeg[position] == Marker && position + 1 < jpeg.Length)
                        {
                            var next = jpeg[position + 1];
                            if (next != 0x00 && next is not (>= 0xD0 and <= 0xD7))
                                break;

                            position += 2;
                            continue;
                        }

                        position++;
                    }

                    jpeg[start..position].CopyTo(output.AsSpan(written));
                    written += position - start;
                }
            }

            if (!complete)
                return false;

            stripped = output.AsSpan(0, written).ToArray();
            return true;
        }
        finally
        {
            Array.Clear(output);
        }
    }

    /// <summary>The markers of the segments of <paramref name="jpeg"/> before its first scan and between its scans (no entropy-coded data): what a test reads to say what is in a file.</summary>
    public static IReadOnlyList<byte> SegmentMarkers(ReadOnlySpan<byte> jpeg)
    {
        var markers = new List<byte>();
        if (jpeg.Length < 4 || jpeg[0] != Marker || jpeg[1] != StartOfImage)
            return markers;

        var position = 2;
        while (position + 1 < jpeg.Length)
        {
            if (jpeg[position] != Marker)
            {
                position++;
                continue;
            }

            var marker = jpeg[position + 1];
            if (marker == Marker || marker == 0x00 || marker is >= 0xD0 and <= 0xD7)
            {
                position++;
                continue;
            }

            markers.Add(marker);
            if (marker == EndOfImage)
                break;

            if (position + 4 > jpeg.Length)
                break;

            var length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (length < 2)
                break;

            position += 2 + length;
        }

        return markers;
    }
}
