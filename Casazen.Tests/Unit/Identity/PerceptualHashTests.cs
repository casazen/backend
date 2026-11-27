using System.Text;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US01/T02 (NF-189): the difference hash of a picture. It is the same for the same picture whoever asks, close for the same card photographed again (a little shifted,
/// a little brighter) and far for the front and the back of the generated card, against the distance of the policy file, not a number of the test. The pictures are built in
/// code (<see cref="DocumentImageFactory"/>).
/// </summary>
public class PerceptualHashTests
{
    private static readonly DocumentFormat Cie = new DocumentFormatRegistry(
        Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, AppContext.BaseDirectory).Get("it-cie-3.0")!;

    /// <summary>The distance the committed policy file calls the same side photographed twice.</summary>
    private static int PolicyDistance()
    {
        Assert.True(IdentityConfigurationJson.TryCanonicalize(Encoding.UTF8.GetBytes(IdentityPolicyTestFiles.ShippedText()), out var canonical));
        return IdentityConfigurationJson.ReadStrict<IdentityDecisionPolicy>(canonical)!.Document.PerceptualDuplicateMaxDistance;
    }

    private static RasterImage Card(DocumentSide side, DocumentImageFactory.Placement? placement = null) =>
        DocumentImageFactory.Render(Cie, side, placement ?? DocumentImageFactory.WholeCard);

    [Fact]
    public void DHash64_ThePictureTwice_IsTheSameHash()
    {
        using var picture = Card(DocumentSide.Front);
        using var copy = new RasterImage(picture.Width, picture.Height, (byte[])picture.Rgb.Clone());

        var first = PerceptualHash.DHash64(picture);

        Assert.Equal(first, PerceptualHash.DHash64(picture));
        Assert.Equal(first, PerceptualHash.DHash64(copy));
    }

    [Fact]
    public void DHash64_APictureOfOneColour_IsZero()
    {
        using var plain = DocumentImageFactory.Plain(137);

        Assert.Equal(0UL, PerceptualHash.DHash64(plain));
    }

    [Fact]
    public void DHash64_AGradient_SetsEveryBitWhenItGetsDarkerToTheRightAndNoneWhenItGetsBrighter()
    {
        var darker = GradientToTheRight(brighter: false);
        var brighter = GradientToTheRight(brighter: true);

        Assert.Equal(ulong.MaxValue, PerceptualHash.DHash64(darker));
        Assert.Equal(0UL, PerceptualHash.DHash64(brighter));
        darker.Dispose();
        brighter.Dispose();
    }

    [Fact]
    public void DHash64_TheFirstPairOfTheTopRow_IsTheMostSignificantBit()
    {
        // Only the two cells of the first pair differ: the left one is brighter, so the bit is set and it is the first of the 64.
        var rgb = new byte[90 * 80 * 3];
        for (var y = 0; y < 80; y++)
        {
            for (var x = 0; x < 90; x++)
            {
                var level = (byte)(y < 10 && x < 10 ? 200 : 100);
                var at = ((y * 90) + x) * 3;
                rgb[at] = rgb[at + 1] = rgb[at + 2] = level;
            }
        }

        using var picture = new RasterImage(90, 80, rgb);

        Assert.Equal(0x8000_0000_0000_0000UL, PerceptualHash.DHash64(picture));
    }

    [Theory]
    [InlineData(0UL, 0UL, 0)]
    [InlineData(0UL, ulong.MaxValue, 64)]
    [InlineData(0b1011UL, 0b0001UL, 2)]
    [InlineData(ulong.MaxValue, 0x7FFF_FFFF_FFFF_FFFFUL, 1)]
    public void Distance_IsTheNumberOfBitsThatDiffer_InEitherOrder(ulong a, ulong b, int expected)
    {
        Assert.Equal(expected, PerceptualHash.Distance(a, b));
        Assert.Equal(expected, PerceptualHash.Distance(b, a));
    }

    [Fact]
    public void DHash64_TheSameCardPhotographedAgainAHairShiftedAndBrighter_IsCloserThanThePolicyDistance()
    {
        // A pixel of 640 in each direction and twelve levels of light. The hash is the plain difference hash: cells of the card whose brightness is nearly the same flip with
        // a larger shift (on the generated card, all text, 3 pixels flip 7 or 8 of the 64 bits), so what a real second photo costs in bits is for the qualification to
        // measure and for the policy file to allow; this test only holds the property that a hair of difference is a few bits.
        using var first = Card(DocumentSide.Front);
        using var shifted = Card(DocumentSide.Front, new DocumentImageFactory.Placement(81, 71, 480, 303));
        using var again = DocumentImageFactory.Brightened(shifted, 12);

        var distance = PerceptualHash.Distance(PerceptualHash.DHash64(first), PerceptualHash.DHash64(again));

        Assert.NotEqual(first.Rgb, again.Rgb);
        Assert.True(distance <= PolicyDistance(), $"distance {distance} against the policy distance {PolicyDistance()}");
    }

    [Fact]
    public void DHash64_TheBrightnessOfThePicture_DoesNotChangeTheHash()
    {
        using var first = Card(DocumentSide.Back);
        using var brighter = DocumentImageFactory.Brightened(first, 12);
        using var darker = DocumentImageFactory.Brightened(first, -12);

        Assert.Equal(PerceptualHash.DHash64(first), PerceptualHash.DHash64(brighter));
        Assert.Equal(PerceptualHash.DHash64(first), PerceptualHash.DHash64(darker));
    }

    [Fact]
    public void DHash64_TheFrontAndTheBackOfTheGeneratedCard_AreFarApart()
    {
        using var front = Card(DocumentSide.Front);
        using var back = Card(DocumentSide.Back);

        var distance = PerceptualHash.Distance(PerceptualHash.DHash64(front), PerceptualHash.DHash64(back));

        Assert.True(distance >= 2 * PolicyDistance(), $"distance {distance} against twice the policy distance {2 * PolicyDistance()}");
    }

    [Theory]
    [InlineData(1280, 960)]
    [InlineData(320, 240)]
    public void DHash64_ThePictureAtAnotherSize_IsTheSameHash(int width, int height)
    {
        // Phones send what their camera makes: the hash must not depend on the resolution of the photo.
        using var original = Card(DocumentSide.Front);
        using var resized = original.Resized(width, height);

        var distance = PerceptualHash.Distance(PerceptualHash.DHash64(original), PerceptualHash.DHash64(resized));

        Assert.True(distance <= PolicyDistance(), $"distance {distance} against the policy distance {PolicyDistance()}");
    }

    [Fact]
    public void DHash64_TheCardTurnedAHalfTurn_IsAnotherPicture()
    {
        using var upright = Card(DocumentSide.Back);
        using var turned = DocumentImageFactory.TurnedHalf(upright);

        var distance = PerceptualHash.Distance(PerceptualHash.DHash64(upright), PerceptualHash.DHash64(turned));

        Assert.True(distance > PolicyDistance());
    }

    [Fact]
    public void DHash64_LeavesThePictureAsItWas()
    {
        using var picture = Card(DocumentSide.Front);
        var before = (byte[])picture.Rgb.Clone();

        PerceptualHash.DHash64(picture);

        Assert.Equal(before, picture.Rgb);
    }

    [Fact]
    public void DHash64_ADisposedPicture_IsRefused()
    {
        var picture = Card(DocumentSide.Front);
        picture.Dispose();

        Assert.Throws<ObjectDisposedException>(() => PerceptualHash.DHash64(picture));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(8, 9)]
    [InlineData(9, 8)]
    public void DHash64_APictureSmallerThanTheThumbnail_StillHasAHash(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        new Random(width * 31 + height).NextBytes(rgb);
        using var picture = new RasterImage(width, height, rgb);

        var first = PerceptualHash.DHash64(picture);

        Assert.Equal(first, PerceptualHash.DHash64(picture));
    }

    [Fact]
    public void ToHex_AndTryParseHex_RoundTripTheSixteenDigitsOfAHash()
    {
        Assert.Equal("0000000000000000", PerceptualHash.ToHex(0));
        Assert.Equal("8000000000000001", PerceptualHash.ToHex(0x8000_0000_0000_0001UL));
        Assert.True(PerceptualHash.TryParseHex("8000000000000001", out var lower));
        Assert.True(PerceptualHash.TryParseHex("FFFFFFFFFFFFFFFE", out var upper));
        Assert.Equal(0x8000_0000_0000_0001UL, lower);
        Assert.Equal(ulong.MaxValue - 1, upper);
        Assert.Equal(upper, ulong.Parse(PerceptualHash.ToHex(upper), System.Globalization.NumberStyles.AllowHexSpecifier));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("8000000000000")]
    [InlineData("80000000000000010")]
    [InlineData("800000000000000g")]
    [InlineData("0x80000000000001")]
    [InlineData(" 800000000000001")]
    public void TryParseHex_AnythingButSixteenHexadecimalDigits_IsRefused(string? text)
    {
        Assert.False(PerceptualHash.TryParseHex(text, out var hash));
        Assert.Equal(0UL, hash);
    }

    /// <summary>A picture whose luminance changes by the same step from one column to the next, towards the right.</summary>
    private static RasterImage GradientToTheRight(bool brighter)
    {
        const int width = 180;
        const int height = 160;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var level = (byte)(brighter ? 20 + x : 220 - x);
                var at = ((y * width) + x) * 3;
                rgb[at] = rgb[at + 1] = rgb[at + 2] = level;
            }
        }

        return new RasterImage(width, height, rgb);
    }
}
