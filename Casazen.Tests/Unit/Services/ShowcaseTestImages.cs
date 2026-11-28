using System.Buffers.Binary;
using System.Text;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Small images written byte by byte for the tests of the showcase media (SP-13b): a JPEG, a PNG and a WebP that carry what a phone adds (the
/// GPS position, a comment, a thumbnail, XMP, trailing bytes). They have the structure of the real thing (segments, chunks, a frame header with a
/// size) and no pixels worth drawing: the code under test reads the structure and never decodes.
/// </summary>
internal static class ShowcaseTestImages
{
    public const string GpsText = "GPSLatitude=45.4642N GPSLongitude=9.1900E";
    public const string CommentText = "taken at Via Roma 1";

    public static byte[] Segment(byte marker, params byte[] payload)
    {
        var length = payload.Length + 2;
        var bytes = new byte[2 + length];
        bytes[0] = 0xFF;
        bytes[1] = marker;
        bytes[2] = (byte)(length >> 8);
        bytes[3] = (byte)length;
        payload.CopyTo(bytes, 4);
        return bytes;
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    public static readonly byte[] Soi = [0xFF, 0xD8];
    public static readonly byte[] Eoi = [0xFF, 0xD9];

    public static byte[] JfifPlain() =>
        Segment(0xE0, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00);

    public static byte[] JfifWithThumbnail() =>
        Segment(0xE0, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0xAA, 0xBB, 0xCC);

    public static byte[] Exif(int orientation, bool withGps)
    {
        var entries = withGps ? 2 : 1;
        var tiff = new List<byte> { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, (byte)entries };

        tiff.AddRange([0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00]);
        if (withGps)
        {
            tiff.AddRange([0x88, 0x25, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x26]);
        }

        tiff.AddRange([0x00, 0x00, 0x00, 0x00]);
        if (withGps)
            tiff.AddRange(Ascii(GpsText));

        return Segment(0xE1, Concat(Ascii("Exif\0\0"), tiff.ToArray()));
    }

    public static byte[] Xmp() =>
        Segment(0xE1, Concat(Ascii("http://ns.adobe.com/xap/1.0/\0"), Ascii("<x:xmpmeta>GPSLatitude 45.46</x:xmpmeta>")));

    public static byte[] Comment() => Segment(0xFE, Ascii(CommentText));

    public static byte[] IccProfile() => Segment(0xE2, Concat(Ascii("ICC_PROFILE\0"), [0x01, 0x01, 0x00, 0x00, 0x00, 0x00]));

    public static byte[] PhotoshopBlock() => Segment(0xED, Ascii("Photoshop 3.0\0" + CommentText));

    public static byte[] Dqt() => Segment(0xDB, Concat([0x00], Enumerable.Repeat((byte)0x10, 64).ToArray()));

    public static byte[] Sof0() =>
        Segment(0xC0, 0x08, 0x00, 0x10, 0x00, 0x20, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01);

    public static byte[] Dht() => Segment(0xC4, 0x00, 0x01, 0x02);

    public static byte[] SosHeader() => Segment(0xDA, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00);

    public static byte[] ScanData() => [0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD0, 0x78];

    public static byte[] Trailing() => Ascii("<script>alert(1)</script>");

    public static byte[] PhoneJpeg(int orientation = 6) =>
        Concat(
            Soi, JfifWithThumbnail(), Exif(orientation, withGps: true), Xmp(), Comment(), PhotoshopBlock(), IccProfile(),
            Dqt(), Sof0(), Dht(), SosHeader(), ScanData(), Eoi, Trailing());

    public static byte[] CleanedPhoneJpeg(int orientation) =>
        Concat(
            Soi,
            Segment(0xE0, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00),
            orientation >= 2 ? OrientationBlock(orientation) : [],
            IccProfile(), Dqt(), Sof0(), Dht(), SosHeader(), ScanData(), Eoi);

    public static byte[] OrientationBlock(int orientation) =>
        Segment(
            0xE1,
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00);

    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] PngChunk(string type, params byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Ascii(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    public static byte[] PngHeader(uint width, uint height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), height);
        data[8] = 8;
        data[9] = 2;
        return PngChunk("IHDR", data);
    }

    public static byte[] PhonePng(uint width = 32, uint height = 16) =>
        Concat(
            PngSignature, PngHeader(width, height),
            PngChunk("tEXt", Ascii("Comment\0" + CommentText)),
            PngChunk("eXIf", Concat([0x4D, 0x4D, 0x00, 0x2A], Ascii(GpsText))),
            PngChunk("sRGB", 0x00),
            PngChunk("iTXt", Ascii("XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta>GPSLatitude</x:xmpmeta>")),
            PngChunk("tIME", 0x07, 0xEA, 0x0A, 0x1F, 0x09, 0x00, 0x00),
            PngChunk("IDAT", 0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01),
            PngChunk("IEND"),
            Trailing());

    public static byte[] CleanedPhonePng(uint width = 32, uint height = 16) =>
        Concat(
            PngSignature, PngHeader(width, height),
            PngChunk("sRGB", 0x00),
            PngChunk("IDAT", 0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01),
            PngChunk("IEND"));

    public static byte[] RiffChunk(string id, params byte[] payload)
    {
        var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
        Ascii(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(chunk, 8);
        return chunk;
    }

    public static byte[] Riff(params byte[][] chunks)
    {
        var body = Concat(chunks);
        var file = new byte[12 + body.Length];
        Ascii("RIFF").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4, 4), (uint)(4 + body.Length));
        Ascii("WEBP").CopyTo(file, 8);
        body.CopyTo(file, 12);
        return file;
    }

    public static byte[] Vp8lPayload() => [0x2F, 0x1F, 0xC0, 0x03, 0x00, 0x10, 0x20, 0x30];

    public static byte[] Vp8xPayload(byte flags) => [flags, 0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x0F, 0x00, 0x00];

    public const byte FlagAlpha = 0x10;
    public const byte FlagExif = 0x08;
    public const byte FlagXmp = 0x04;
    public const byte FlagAnimation = 0x02;

    public static byte[] PhoneWebP() =>
        Concat(
            Riff(
                RiffChunk("VP8X", Vp8xPayload((byte)(FlagAlpha | FlagExif | FlagXmp))),
                RiffChunk("VP8L", Vp8lPayload()),
                RiffChunk("EXIF", Ascii(GpsText)),
                RiffChunk("XMP ", Ascii("<x:xmpmeta>GPSLatitude</x:xmpmeta>"))),
            Trailing());

    public static byte[] CleanedPhoneWebP() =>
        Riff(
            RiffChunk("VP8X", Vp8xPayload(FlagAlpha)),
            RiffChunk("VP8L", Vp8lPayload()));
}
