using System.Buffers.Binary;
using System.IO.Compression;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

internal static class CardPngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var stride = checked(image.Width * 3);
        var scanlines = new byte[checked(image.Height * (stride + 1))];
        MemoryStream? deflated = null;
        try
        {
            for (var row = 0; row < image.Height; row++)
                Buffer.BlockCopy(image.Rgb, row * stride, scanlines, (row * (stride + 1)) + 1, stride);

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
            header[8] = 8;
            header[9] = 2;
            header[10] = 0;
            header[11] = 0;
            header[12] = 0;
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
