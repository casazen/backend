using System.Buffers.Binary;

namespace Casazen.Core.Branding;

public enum ImageFormat
{
    Png,
    Jpeg,
    WebP,
}

public sealed record ImageHeaderInfo(ImageFormat Format, int Width, int Height)
{
    public string ContentType => Format switch
    {
        ImageFormat.Png => "image/png",
        ImageFormat.Jpeg => "image/jpeg",
        _ => "image/webp",
    };

    public string Extension => Format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        _ => ".webp",
    };
}

public static class ImageHeaderReader
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageHeaderInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngSignature))
            return ReadPng(data);
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return ReadJpeg(data);
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return ReadWebP(data);
        return null;
    }

    private static ImageHeaderInfo? ReadPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;

        var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        return Create(ImageFormat.Png, width, height);
    }

    private static ImageHeaderInfo? ReadJpeg(ReadOnlySpan<byte> data)
    {
        var position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
                return null;

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            position += 2;
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;

            if (marker == 0xDA || marker == 0xD9)
                return null;

            if (position + 2 > data.Length)
                return null;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (segmentLength < 2)
                return null;

            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (position + 7 > data.Length)
                    return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 5, 2));
                return Create(ImageFormat.Jpeg, width, height);
            }

            position += segmentLength;
        }

        return null;
    }

    private static ImageHeaderInfo? ReadWebP(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
            return null;

        var chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A)
                return null;
            var width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3FFF;
            return Create(ImageFormat.WebP, (uint)width, (uint)height);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (data[20] != 0x2F)
                return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21, 4));
            return Create(ImageFormat.WebP, (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            var width = (uint)(data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            var height = (uint)(data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            return Create(ImageFormat.WebP, width, height);
        }

        return null;
    }

    private static ImageHeaderInfo? Create(ImageFormat format, uint width, uint height) =>
        width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue
            ? null
            : new ImageHeaderInfo(format, (int)width, (int)height);
}
