using Casazen.Core.Branding;
using Xunit;

namespace Casazen.Tests.Unit.Branding;

public class ImageHeaderReaderTests
{
    [Fact]
    public void TryRead_Png_ReturnsFormatAndSize()
    {
        var info = ImageHeaderReader.TryRead(TestImageBytes.Png(640, 200));

        Assert.Equal(new ImageHeaderInfo(ImageFormat.Png, 640, 200), info);
        Assert.Equal("image/png", info!.ContentType);
        Assert.Equal(".png", info.Extension);
    }

    [Fact]
    public void TryRead_JpegWithAppSegmentBeforeFrame_ReturnsSizeFromFrameHeader()
    {
        var info = ImageHeaderReader.TryRead(TestImageBytes.Jpeg(1920, 1080));

        Assert.Equal(new ImageHeaderInfo(ImageFormat.Jpeg, 1920, 1080), info);
        Assert.Equal(".jpg", info!.Extension);
    }

    [Theory]
    [InlineData("extended")]
    [InlineData("lossless")]
    [InlineData("lossy")]
    public void TryRead_WebPVariants_ReturnsSize(string variant)
    {
        var bytes = variant switch
        {
            "extended" => TestImageBytes.WebPExtended(2400, 900),
            "lossless" => TestImageBytes.WebPLossless(2400, 900),
            _ => TestImageBytes.WebPLossy(2400, 900),
        };

        var info = ImageHeaderReader.TryRead(bytes);

        Assert.Equal(new ImageHeaderInfo(ImageFormat.WebP, 2400, 900), info);
        Assert.Equal("image/webp", info!.ContentType);
    }

    [Fact]
    public void TryRead_Svg_ReturnsNull()
    {
        Assert.Null(ImageHeaderReader.TryRead(TestImageBytes.Svg()));
    }

    [Fact]
    public void TryRead_Gif_ReturnsNull()
    {
        Assert.Null(ImageHeaderReader.TryRead(TestImageBytes.Gif(200, 100)));
    }

    [Fact]
    public void TryRead_TruncatedPng_ReturnsNull()
    {
        Assert.Null(ImageHeaderReader.TryRead(TestImageBytes.Png(640, 200).AsSpan(0, 20)));
    }

    [Fact]
    public void TryRead_JpegWithoutFrameHeader_ReturnsNull()
    {
        byte[] startOfScanFirst = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0, 0, 0];

        Assert.Null(ImageHeaderReader.TryRead(startOfScanFirst));
    }

    [Fact]
    public void TryRead_ZeroWidthPng_ReturnsNull()
    {
        Assert.Null(ImageHeaderReader.TryRead(TestImageBytes.Png(0, 200)));
    }
}
