using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T02: the locator of the MRZ band, on pictures built in memory (never a file): it finds the lines of text in the lower part of a
/// page, groups the ones that look like an MRZ, and says nothing about a page that has none. The geometry only proposes; the check digits decide.
/// </summary>
public class MrzImageLocatorTests
{
    private const int PageWidth = 1200;
    private const int PageHeight = 800;
    private const byte Ground = 235;
    private const byte Ink = 25;

    private static readonly MrzImageLocator Locator = new(Options.Create(new MrzOcrOptions()));

    /// <summary>A light page with lines of 30 rows made of dark blocks 12 wide every 20 pixels, from x = 100 to 1100 (50 characters), at the given tops.</summary>
    private static GrayBitmap Page(params int[] lineTops)
    {
        var pixels = new byte[PageWidth * PageHeight];
        Array.Fill(pixels, Ground);
        foreach (var top in lineTops)
        {
            for (var x = 100; x + 12 <= 1100; x += 20)
            {
                for (var y = top; y < top + 30; y++)
                {
                    for (var dx = 0; dx < 12; dx++)
                        pixels[(y * PageWidth) + x + dx] = Ink;
                }
            }
        }

        return new GrayBitmap(PageWidth, PageHeight, pixels);
    }

    [Fact]
    public void Locate_ThreeLinesAtTheBottom_AreOneBlockOfThree()
    {
        using var page = Page(560, 610, 660);

        using var location = Locator.Locate(page, CancellationToken.None);

        Assert.Equal(3, location.Lines.Count);
        Assert.Equal(new[] { 0, 1, 2 }, location.Groups[0].ToArray());
        Assert.InRange(location.Lines[0].Top, 556, 564);
        Assert.InRange(location.Lines[1].Top, 606, 614);
        Assert.InRange(location.Lines[2].Top, 656, 664);
        Assert.InRange(location.Lines[0].Left, 92, 108);
        Assert.InRange(location.Lines[0].Right, 1084, 1100);
        Assert.All(location.Lines, line => Assert.Equal(0.0, line.GlareShare));
        Assert.All(location.Lines, line => Assert.True(line.BlurRatio < 0.6, "crisp blocks are not out of focus"));
    }

    [Fact]
    public void Locate_TheStrips_AreStretchedAndBinarised()
    {
        using var page = Page(560, 610);

        using var location = Locator.Locate(page, CancellationToken.None);

        Assert.Equal(2, location.Lines.Count);
        var line = location.Lines[0];
        Assert.InRange(line.Gray.Height, 60, 100);
        Assert.Equal(0, line.Gray.Pixels.Min());
        Assert.Equal(255, line.Gray.Pixels.Max());
        Assert.All(line.Binary.Pixels, pixel => Assert.True(pixel == 0 || pixel == 255));
        Assert.Contains(line.Binary.Pixels, pixel => pixel == 0);
        Assert.Equal((line.Gray.Width, line.Gray.Height), (line.Binary.Width, line.Binary.Height));
    }

    [Fact]
    public void Locate_StripsAreZeroedWhenTheLocationIsDisposed()
    {
        using var page = Page(560, 610);
        var location = Locator.Locate(page, CancellationToken.None);
        var held = location.Lines.SelectMany(line => new[] { line.Gray.Pixels, line.Binary.Pixels }).ToList();
        Assert.Contains(held, pixels => pixels.Any(p => p != 0));

        location.Dispose();

        Assert.All(held, pixels => Assert.All(pixels, p => Assert.Equal(0, p)));
    }

    [Fact]
    public void Locate_ABlankPage_FindsNothing()
    {
        var pixels = new byte[PageWidth * PageHeight];
        Array.Fill(pixels, Ground);
        using var page = new GrayBitmap(PageWidth, PageHeight, pixels);

        using var location = Locator.Locate(page, CancellationToken.None);

        Assert.Empty(location.Lines);
        Assert.Empty(location.Groups);
    }

    [Fact]
    public void Locate_OneLineAlone_IsNoBlock()
    {
        using var page = Page(600);

        using var location = Locator.Locate(page, CancellationToken.None);

        Assert.Empty(location.Groups);
    }

    [Fact]
    public void Locate_ThePictureIsTooSmallToRead_FindsNothing()
    {
        using var page = new GrayBitmap(100, 50, new byte[100 * 50]);

        using var location = Locator.Locate(page, CancellationToken.None);

        Assert.Empty(location.Lines);
    }

    [Fact]
    public void Locate_Cancelled_Stops()
    {
        using var page = Page(560, 610);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => Locator.Locate(page, cancelled.Token));
    }

    [Fact]
    public void Tilt_OfStraightLines_IsZero()
    {
        using var page = Page(560, 610, 660);

        Assert.Equal(0.0, MrzImageLocator.EstimateTiltDegrees(page));
    }

    [Fact]
    public void Tilt_OfLinesThatDescendToTheRight_IsPositive()
    {
        using var straight = Page(560, 610, 660);
        using var tilted = straight.Rotated(3.0);

        var tilt = MrzImageLocator.EstimateTiltDegrees(tilted);

        Assert.InRange(tilt, 2.0, 4.0);
    }

    [Fact]
    public void InkMask_MarksTheDarkPixelsOfALightGround()
    {
        var pixels = new byte[21 * 21];
        Array.Fill(pixels, (byte)200);
        for (var y = 9; y <= 11; y++)
        {
            for (var x = 9; x <= 11; x++)
                pixels[(y * 21) + x] = 20;
        }

        using var picture = new GrayBitmap(21, 21, pixels);

        var mask = MrzImageLocator.InkMask(picture, 8, 0.85);

        Assert.Equal(9, mask.Count(marked => marked));
        Assert.True(mask[(10 * 21) + 10]);
        Assert.False(mask[0]);
    }

    [Fact]
    public void GroupLines_NeedsTheSameHeightExtentAndARegularSpacing()
    {
        var regular = new List<MrzImageLocator.Run>
        {
            new(560, 589, 100, 1091),
            new(610, 639, 100, 1091),
            new(660, 689, 100, 1091),
        };
        var shifted = new List<MrzImageLocator.Run>
        {
            new(560, 589, 100, 1091),
            new(610, 639, 100, 1091),
            new(660, 689, 500, 1091),
        };
        var far = new List<MrzImageLocator.Run>
        {
            new(300, 329, 100, 1091),
            new(610, 639, 100, 1091),
        };
        var tall = new List<MrzImageLocator.Run>
        {
            new(560, 589, 100, 1091),
            new(610, 700, 100, 1091),
        };

        Assert.Equal(new[] { 0, 1, 2 }, MrzImageLocator.GroupLines(regular, PageWidth, PageHeight)[0].ToArray());
        Assert.DoesNotContain(MrzImageLocator.GroupLines(shifted, PageWidth, PageHeight), group => group.Contains(2));
        Assert.Empty(MrzImageLocator.GroupLines(far, PageWidth, PageHeight));
        Assert.Empty(MrzImageLocator.GroupLines(tall, PageWidth, PageHeight));
    }

    [Fact]
    public void BlurRatio_IsLowForCrispEdgesAndHighForSmearedOnes()
    {
        var crisp = new byte[80 * 20];
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 80; x++)
                crisp[(y * 80) + x] = (x / 20) % 2 == 0 ? (byte)0 : (byte)255;
        }

        var smeared = new byte[crisp.Length];
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 80; x++)
            {
                var sum = 0;
                for (var k = -4; k <= 4; k++)
                    sum += crisp[(y * 80) + Math.Clamp(x + k, 0, 79)];

                smeared[(y * 80) + x] = (byte)(sum / 9);
            }
        }

        using var sharpPicture = new GrayBitmap(80, 20, crisp);
        using var blurredPicture = new GrayBitmap(80, 20, smeared);

        Assert.True(MrzImageLocator.BlurRatio(sharpPicture) < 0.45);
        Assert.True(MrzImageLocator.BlurRatio(blurredPicture) > 0.7);
        Assert.Equal(1.0, MrzImageLocator.BlurRatio(new GrayBitmap(10, 10, new byte[100])));
    }

    [Fact]
    public void SaturatedShare_CountsThePixelsOfTheTopOfTheRange()
    {
        var pixels = new byte[100];
        Array.Fill(pixels, (byte)100);
        for (var i = 0; i < 25; i++)
            pixels[i] = 255;

        pixels[30] = 249;
        using var picture = new GrayBitmap(10, 10, pixels);

        Assert.Equal(0.25, MrzImageLocator.SaturatedShare(picture), 6);
    }

    [Fact]
    public void Stretch_TakesTheDarkestAndLightestToTheEndsOfTheRange()
    {
        var pixels = new byte[100];
        for (var i = 0; i < 100; i++)
            pixels[i] = i < 50 ? (byte)100 : (byte)150;

        using var picture = new GrayBitmap(10, 10, pixels);
        using var stretched = MrzImageLocator.Stretch(picture);
        using var flat = MrzImageLocator.Stretch(new GrayBitmap(2, 2, [100, 105, 100, 105]));

        Assert.Equal(0, stretched.Pixels[0]);
        Assert.Equal(255, stretched.Pixels[99]);
        Assert.Equal(new byte[] { 100, 105, 100, 105 }, flat.Pixels);
    }
}
