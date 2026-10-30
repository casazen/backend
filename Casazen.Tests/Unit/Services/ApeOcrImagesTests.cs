using System.Text;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ApeOcrImagesTests
{
    [Fact]
    public void GrayImage_ReadsThePgmOfPdftoppm_NegatesAndCutsIt()
    {
        // 4 x 3 pixels, with a comment line as pdftoppm may write one.
        var pixels = new byte[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110 };
        var pgm = Encoding.ASCII.GetBytes("P5\n# poppler\n4 3\n255\n").Concat(pixels).ToArray();

        var image = GrayImage.Read(pgm);

        Assert.Equal((4, 3), (image.Width, image.Height));
        Assert.Equal(new byte[] { 255, 245, 235, 225 }, image.Negated().Pixels[..4]);
        Assert.Equal(pixels, GrayImage.Read(image.ToPgm()).Pixels);
        Assert.Null(image.Crop(1, 1, 3, 3)); // smaller than a letter: nothing to read
    }

    [Fact]
    public void GrayImage_CropIsCutToThePage()
    {
        var image = GrayImage.Read(Encoding.ASCII.GetBytes("P5 20 20 255\n").Concat(Enumerable.Range(0, 400).Select(i => (byte)i)).ToArray());

        var zone = image.Crop(-5, 10, 50, 30);

        Assert.Equal((20, 10), (zone!.Width, zone.Height));
        Assert.Equal(image.Pixels[200], zone.Pixels[0]);
    }

    [Fact]
    public void GrayImage_SomethingElse_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => GrayImage.Read("P6\n4 3\n255\n"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => GrayImage.Read("P5\n4 3\n255\nab"u8.ToArray()));
    }

    [Fact]
    public void Anchors_ClassBoxIsNarrowAndTall_DateLineIsWide_SameZoneOnce()
    {
        var words = OcrWord.ParseTsv("""
            level	page_num	block_num	par_num	line_num	word_num	left	top	width	height	conf	text
            5	1	1	1	1	1	1100	1500	160	40	91	CLASSE
            5	1	1	1	1	2	1102	1502	158	40	88	CLASSE
            5	1	2	1	1	1	300	2200	240	30	90	emissione
            5	1	3	1	1	1	700	900	90	30	95	Comune
            """);

        var boxes = ApeAnchors.Boxes(words);

        Assert.Equal([(1060, 1340, 1300, 1820), (0, 2170, 1080, 2260)], boxes);
    }
}
