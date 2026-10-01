using System.Buffers.Binary;
using System.Text;

namespace Casazen.Tests.Unit.Branding;

/// <summary>
/// Headers of PNG, JPEG and WebP files with a chosen pixel size, padded to a chosen length: enough for
/// <c>ImageHeaderReader</c>, which never decodes the pixels.
/// </summary>
internal static class TestImageBytes
{
    public static byte[] Png(int width, int height, int totalLength = 64)
    {
        var data = new byte[Math.Max(totalLength, 33)];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(data, 12);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)height);
        data[24] = 8; // bit depth
        data[25] = 6; // RGBA
        return data;
    }

    public static byte[] Jpeg(int width, int height, int totalLength = 64)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        // APP0 (JFIF), 16 bytes including the length.
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]);
        bytes.AddRange(Encoding.ASCII.GetBytes("JFIF\0"));
        bytes.AddRange(new byte[9]);
        // SOF0: length 17, precision 8, height, width, 3 components.
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        bytes.AddRange([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03]);
        bytes.AddRange(new byte[9]);
        while (bytes.Count < totalLength)
            bytes.Add(0);
        return [.. bytes];
    }

    /// <summary>Extended WebP (VP8X chunk), the layout of most encoders' output with metadata.</summary>
    public static byte[] WebPExtended(int width, int height) =>
        WebP("VP8X", chunk =>
        {
            chunk[0] = 0; // flags
            WriteUInt24(chunk.AsSpan(4), width - 1);
            WriteUInt24(chunk.AsSpan(7), height - 1);
        });

    public static byte[] WebPLossless(int width, int height) =>
        WebP("VP8L", chunk =>
        {
            chunk[0] = 0x2F;
            var bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(1), bits);
        });

    public static byte[] WebPLossy(int width, int height) =>
        WebP("VP8 ", chunk =>
        {
            chunk[3] = 0x9D;
            chunk[4] = 0x01;
            chunk[5] = 0x2A;
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(6), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(8), (ushort)height);
        });

    public static byte[] Svg() =>
        Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\"><script>alert(1)</script></svg>");

    public static byte[] Gif(int width, int height)
    {
        var data = new byte[32];
        Encoding.ASCII.GetBytes("GIF89a").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)height);
        return data;
    }

    private static byte[] WebP(string fourCc, Action<byte[]> writeChunk)
    {
        var chunk = new byte[24];
        writeChunk(chunk);
        var data = new byte[20 + chunk.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(data, 8);
        Encoding.ASCII.GetBytes(fourCc).CopyTo(data, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)chunk.Length);
        chunk.CopyTo(data, 20);
        return data;
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }
}
