using System.Buffers.Binary;
using System.IO.Compression;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// Writes a picture held in memory as a PNG (8 bits, red-green-blue, no interlace), so a reader that takes an encoded picture (<c>IMrzExtractionService</c> reads a
/// stream, as the upload gives it) can read a card that is already decoded and set upright (E71/F01/US02/T01). Lossless, in memory only: no file, no library, no
/// network. The pixels it copies are zeroed before it returns; the bytes it returns are the caller's to zero (<c>ImageBytes.Zero</c>).
/// </summary>
internal static class CardPngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>The PNG of <paramref name="image"/>.</summary>
    public static byte[] Encode(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var stride = checked(image.Width * 3);
        var scanlines = new byte[checked(image.Height * (stride + 1))];
        MemoryStream? deflated = null;
        try
        {
            // Every scanline starts with its filter type, 0 (none).
            for (var row = 0; row < image.Height; row++)
                Buffer.BlockCopy(image.Rgb, row * stride, scanlines, (row * (stride + 1)) + 1, stride);

            // The capacity holds the worst case of the compressor, so the buffer is never replaced by a bigger one (a copy that could not be zeroed).
            deflated = new MemoryStream(scanlines.Length + (scanlines.Length / 100) + 128);
            using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(scanlines, 0, scanlines.Length);

            var data = deflated.GetBuffer().AsSpan(0, (int)deflated.Length);
            var png = new byte[Signature.Length + (12 + 13) + (12 + data.Length) + 12];
            var position = 0;
            Signature.CopyTo(png, position);
            position += Signature.Length;

            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
            header[8] = 8; // bit depth
            header[9] = 2; // colour type: red, green, blue
            header[10] = 0; // compression method
            header[11] = 0; // filter method
            header[12] = 0; // no interlace
            position = WriteChunk(png, position, "IHDR"u8, header);
            position = WriteChunk(png, position, "IDAT"u8, data);
            WriteChunk(png, position, "IEND"u8, []);
            return png;
        }
        finally
        {
            Array.Clear(scanlines);
            if (deflated is not null)
            {
                Array.Clear(deflated.GetBuffer());
                deflated.Dispose();
            }
        }
    }

    private static int WriteChunk(byte[] target, int position, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(position), (uint)data.Length);
        type.CopyTo(target.AsSpan(position + 4));
        data.CopyTo(target.AsSpan(position + 8));
        var crc = Crc(target.AsSpan(position + 4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(position + 8 + data.Length), crc);
        return position + 12 + data.Length;
    }

    /// <summary>The CRC-32 of the PNG specification (the one of zlib), over the type and the data of a chunk.</summary>
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var crc = n;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;

            table[n] = crc;
        }

        return table;
    }
}
