using System.Text;
using Casazen.Infrastructure.Services;
using Xunit;
using static Casazen.Tests.Unit.Services.ShowcaseTestImages;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="ShowcaseImageSanitizer"/> (SP-13b): an image of a showcase is written again without its metadata, in plain managed code and
/// without decoding the picture. The tests build the files byte by byte (<see cref="ShowcaseTestImages"/>) and compare the result with the bytes
/// it must be, written in the test and not taken from the code. What matters most: the GPS position, the comments, the thumbnails and whatever
/// follows the end of the image do not get through; the orientation of a photo taken on its side does, as a block of one tag.
/// </summary>
public class ShowcaseImageSanitizerTests
{
    // ─── JPEG ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(6)]
    [InlineData(3)]
    [InlineData(8)]
    public void Jpeg_APhoneFile_LosesItsMetadataAndKeepsTheOrientationAsOneTag(int orientation)
    {
        var cleaned = ShowcaseImageSanitizer.Clean(PhoneJpeg(orientation), "image/jpeg");

        Assert.Equal(CleanedPhoneJpeg(orientation), cleaned);
        var text = Encoding.Latin1.GetString(cleaned!);
        Assert.DoesNotContain("GPS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Via Roma", text, StringComparison.Ordinal);
        Assert.DoesNotContain("script", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xmpmeta", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jpeg_TheOrientationThatIsKept_IsReadByTheSizeReader_SoThePictureIsDrawnUpright()
    {
        var cleaned = ShowcaseImageSanitizer.Clean(PhoneJpeg(orientation: 6), "image/jpeg")!;

        // The frame is 32 wide and 16 high; with orientation 6 the browser draws it 16 by 32.
        using var stream = new MemoryStream(cleaned);
        var size = await ImageSizeReader.ReadAsync(stream);
        Assert.Equal((16, 32), size);
    }

    [Fact]
    public void Jpeg_AnUprightPhoto_GetsNoMetadataBlockAtAll()
    {
        var cleaned = ShowcaseImageSanitizer.Clean(PhoneJpeg(orientation: 1), "image/jpeg");

        Assert.Equal(CleanedPhoneJpeg(orientation: 1), cleaned);

        // No APP1 segment: the marker FF E1 is nowhere.
        Assert.True(cleaned!.AsSpan().IndexOf(new byte[] { 0xFF, 0xE1 }) < 0);
    }

    [Fact]
    public void Jpeg_TheThumbnailOfTheJfifHeaderIsNotKept()
    {
        var cleaned = ShowcaseImageSanitizer.Clean(Concat(Soi, JfifWithThumbnail(), Dqt(), Sof0(), SosHeader(), ScanData(), Eoi), "image/jpeg")!;

        // The header is 18 bytes with the marker, the thumbnail size is 0 by 0 and its three bytes of color are gone.
        Assert.Equal(new byte[] { 0xFF, 0xE0, 0x00, 0x10 }, cleaned[2..6]);
        Assert.Equal(new byte[] { 0x00, 0x00 }, cleaned[18..20]);
        Assert.DoesNotContain((byte)0xAA, cleaned);
        Assert.DoesNotContain((byte)0xBB, cleaned);
    }

    [Fact]
    public void Jpeg_AFileCutOffInTheDataOfItsScan_IsClosedWithAnEndOfImage()
    {
        var cut = Concat(Soi, Dqt(), Sof0(), SosHeader(), ScanData());

        var cleaned = ShowcaseImageSanitizer.Clean(cut, "image/jpeg");

        Assert.Equal(Concat(Soi, Dqt(), Sof0(), SosHeader(), ScanData(), Eoi), cleaned);
    }

    [Fact]
    public void Jpeg_AProgressiveFileWithSeveralScans_KeepsEveryScan()
    {
        var progressive = Concat(Soi, Dqt(), Segment(0xC2, 0x08, 0x00, 0x10, 0x00, 0x20, 0x01, 0x01, 0x11, 0x00), Dht(), SosHeader(), ScanData(), Dht(), SosHeader(), ScanData(), Eoi);

        Assert.Equal(progressive, ShowcaseImageSanitizer.Clean(progressive, "image/jpeg"));
    }

    [Fact]
    public void Jpeg_WithNoFrameHeader_OrWithNoScan_IsRefused()
    {
        Assert.Null(ShowcaseImageSanitizer.Clean(Concat(Soi, Dqt(), SosHeader(), ScanData(), Eoi), "image/jpeg"));
        Assert.Null(ShowcaseImageSanitizer.Clean(Concat(Soi, Dqt(), Sof0(), Eoi), "image/jpeg"));
    }

    [Fact]
    public void Jpeg_ASegmentThatRunsPastTheEndOfTheFile_IsRefused()
    {
        var broken = Concat(Soi, [0xFF, 0xE1, 0xFF, 0xFF], Ascii("Exif"));

        Assert.Null(ShowcaseImageSanitizer.Clean(broken, "image/jpeg"));
    }

    [Fact]
    public void Jpeg_ATextThatOnlyStartsLikeAJpeg_IsRefused()
    {
        Assert.Null(ShowcaseImageSanitizer.Clean(Concat(Soi, Ascii("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>")), "image/jpeg"));
    }

    // ─── PNG ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Png_APhoneFile_LosesItsTextItsExifItsTimeAndWhatFollowsTheEnd()
    {
        var cleaned = ShowcaseImageSanitizer.Clean(PhonePng(), "image/png");

        Assert.Equal(CleanedPhonePng(), cleaned);
        var text = Encoding.Latin1.GetString(cleaned!);
        Assert.DoesNotContain("GPS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Via Roma", text, StringComparison.Ordinal);
        Assert.DoesNotContain("script", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Png_WithNoEnd_OrNoData_OrNoHeaderFirst_IsRefused()
    {
        var idat = PngChunk("IDAT", 0x78, 0x9C, 0x63, 0x00);

        // Cut short: no IEND.
        Assert.Null(ShowcaseImageSanitizer.Clean(Concat(PngSignature, PngHeader(32, 16), idat), "image/png"));

        // No picture data.
        Assert.Null(ShowcaseImageSanitizer.Clean(Concat(PngSignature, PngHeader(32, 16), PngChunk("IEND")), "image/png"));

        // The header is not the first chunk.
        Assert.Null(ShowcaseImageSanitizer.Clean(Concat(PngSignature, PngChunk("tEXt", Ascii("a\0b")), PngHeader(32, 16), idat, PngChunk("IEND")), "image/png"));
    }

    [Fact]
    public void Png_AChunkThatRunsPastTheEnd_IsRefused()
    {
        var broken = Concat(PngSignature, PngHeader(32, 16), [0x00, 0x10, 0x00, 0x00], Ascii("IDAT"), [0x01, 0x02]);

        Assert.Null(ShowcaseImageSanitizer.Clean(broken, "image/png"));
    }

    // ─── WebP ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WebP_APhoneFile_LosesItsExifItsXmpAndTheirFlags_AndWhatFollowsTheContainer()
    {
        var cleaned = ShowcaseImageSanitizer.Clean(PhoneWebP(), "image/webp");

        Assert.Equal(CleanedPhoneWebP(), cleaned);
        var text = Encoding.Latin1.GetString(cleaned!);
        Assert.DoesNotContain("GPS", text, StringComparison.Ordinal);
        Assert.DoesNotContain("script", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WebP_ASimpleFile_IsKeptAsItIs()
    {
        var simple = Riff(RiffChunk("VP8L", Vp8lPayload()));

        Assert.Equal(simple, ShowcaseImageSanitizer.Clean(simple, "image/webp"));
    }

    [Fact]
    public void WebP_AnAnimation_OrAFileWithNoPicture_IsRefused()
    {
        // The flag of the animation, and the chunks of one.
        Assert.Null(ShowcaseImageSanitizer.Clean(Riff(RiffChunk("VP8X", Vp8xPayload(FlagAnimation)), RiffChunk("VP8L", Vp8lPayload())), "image/webp"));
        Assert.Null(ShowcaseImageSanitizer.Clean(Riff(RiffChunk("VP8X", Vp8xPayload(0)), RiffChunk("ANIM", [0, 0, 0, 0, 0, 0]), RiffChunk("ANMF", Vp8lPayload())), "image/webp"));

        // A container with no picture, or with two.
        Assert.Null(ShowcaseImageSanitizer.Clean(Riff(RiffChunk("EXIF", Ascii(GpsText))), "image/webp"));
        Assert.Null(ShowcaseImageSanitizer.Clean(Riff(RiffChunk("VP8L", Vp8lPayload()), RiffChunk("VP8L", Vp8lPayload())), "image/webp"));
    }

    [Fact]
    public void WebP_ASizeThatRunsPastTheFile_IsRefused()
    {
        var file = Riff(RiffChunk("VP8L", Vp8lPayload()));

        Assert.Null(ShowcaseImageSanitizer.Clean(file[..^4], "image/webp"));
    }

    [Fact]
    public void AnotherType_IsNeverCleaned()
    {
        Assert.Null(ShowcaseImageSanitizer.Clean(PhoneJpeg(), "image/svg+xml"));
        Assert.Null(ShowcaseImageSanitizer.Clean(PhonePng(), "image/jpeg"));
    }
}
