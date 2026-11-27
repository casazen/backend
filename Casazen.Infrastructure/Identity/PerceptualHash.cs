using System.Globalization;
using System.Numerics;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The difference hash of a picture (E71/F01/US01/T02, NF-189): 64 bits that say, for 64 pairs of neighbouring parts of the picture, which of the two is the brighter. Two
/// photos of the same side of a card differ in some bits (measured below: a few for a copy at another size or a little shifted), the two sides of a card, or two cards, in
/// more. The flow uses it with the SHA-256 of the bytes to refuse the same side sent twice (<c>document_side_duplicate</c>, <see cref="DocumentSideClassifier"/>); the distance
/// that counts as the same picture is <c>policy.document.perceptualDuplicateMaxDistance</c>, never a number here.
/// <para>
/// <b>What it is.</b> The picture is reduced to a thumbnail of 9 by 8 cells (each cell the mean luminance of the pixels it covers, ITU-R BT.601 as
/// <c>RasterImage.ToGray</c> has it); each of the 8 rows gives 8 bits, one for each pair of neighbouring cells: 1 when the left cell is brighter than the right one,
/// 0 otherwise (a tie is 0, so a plain picture is 0). The bits are in reading order, the first pair of the top row being the most significant bit. The result depends
/// on nothing but the pixels: it is the same on every machine and every run. A turned picture has another hash.
/// </para>
/// <para>
/// <b>What it costs, measured.</b> On the card <c>DocumentImageFactory</c> generates (480 by 303 pixels in a picture of 640 by 480, all text, so many neighbouring cells have
/// nearly the same brightness and a near-tie flips with a small change; <c>PerceptualHashTests</c> holds every figure). The same card at another size is close, not the same:
/// the front flips 0 bits at 1280 by 960, 960 by 720 and 320 by 240 and 5 at 160 by 120; the back (a dense MRZ band) flips 2, 2, 5 and 4. More light flips none. A shift of the
/// card by 1, 2 and 3 pixels (both ways) flips 3, 7 and 7 bits of the front (11 for 8 pixels) and 5, 6 and 8 of the back (14 for 8 pixels). A half turn flips 14 (front) and 13
/// (back), the front against the back 15. So against the provisional distance of the policy (6) a second shot of the same side taken by hand can already be out of reach (the
/// front shifted by 2 pixels, the back by 3): until the qualification measures it on real photos (E74), the SHA-256 is what says it is the same file, and the hash only
/// catches the very same shot sent again, resized or a little shifted.
/// </para>
/// <para>
/// <b>Privacy.</b> A hash is a derived value of the document: the flow keeps it in <c>DocumentCheckJson</c> only while the verification runs (it clears it at the closure,
/// E73) and never logs it. This class has no state, no logger and no I/O. It allocates 1 KB on the stack, never a copy of the picture, and leaves the picture as it was.
/// </para>
/// </summary>
public static class PerceptualHash
{
    /// <summary>The thumbnail is this many cells wide: 9 cells make 8 comparisons in a row.</summary>
    private const int Columns = 9;

    /// <summary>The thumbnail is this many cells high.</summary>
    private const int Rows = 8;

    /// <summary>The length of the text of a hash: 64 bits in hexadecimal.</summary>
    private const int HexLength = 16;

    /// <summary>The 64-bit difference hash of <paramref name="image"/> (see the class). A picture that has been disposed is <see cref="ObjectDisposedException"/>.</summary>
    public static ulong DHash64(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var width = image.Width;
        var height = image.Height;
        var rgb = image.Rgb;
        if (rgb.Length != checked(width * height * 3))
            throw new ObjectDisposedException(nameof(RasterImage));

        Span<long> sums = stackalloc long[Columns * Rows];
        Span<long> counts = stackalloc long[Columns * Rows];
        for (var row = 0; row < Rows; row++)
        {
            var top = (int)((long)row * height / Rows);
            var bottom = Math.Max(top + 1, (int)((long)(row + 1) * height / Rows));
            for (var column = 0; column < Columns; column++)
            {
                var left = (int)((long)column * width / Columns);
                var right = Math.Max(left + 1, (int)((long)(column + 1) * width / Columns));
                long sum = 0;
                for (var y = top; y < bottom; y++)
                {
                    var at = ((y * width) + left) * 3;
                    for (var x = left; x < right; x++, at += 3)
                        sum += ((rgb[at] * 299) + (rgb[at + 1] * 587) + (rgb[at + 2] * 114) + 500) / 1000;
                }

                sums[(row * Columns) + column] = sum;
                counts[(row * Columns) + column] = (long)(bottom - top) * (right - left);
            }
        }

        // The left cell is brighter than the right one when sum / count is larger; compared by cross-multiplication, so no rounding decides.
        ulong hash = 0;
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns - 1; column++)
            {
                var left = (row * Columns) + column;
                hash <<= 1;
                if (sums[left] * counts[left + 1] > sums[left + 1] * counts[left])
                    hash |= 1;
            }
        }

        return hash;
    }

    /// <summary>The Hamming distance of two hashes: how many of the 64 bits differ (0 the same picture, 64 the opposite one).</summary>
    public static int Distance(ulong a, ulong b)
    {
        return BitOperations.PopCount(a ^ b);
    }

    /// <summary>A hash as 16 lower case hexadecimal digits, the most significant first: the form the flow keeps it in (the SHA-256 it keeps next to it is hexadecimal as well).</summary>
    public static string ToHex(ulong hash)
    {
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A hash from its 16 hexadecimal digits, upper or lower case; false for anything else (a null, a blank, a different length, another character). On false the flow has no
    /// hash for that picture: its <see cref="DocumentPictureFingerprint"/> holds null, never the 0 this method leaves in <paramref name="hash"/>, which is the hash of a plain picture.
    /// </summary>
    public static bool TryParseHex(string? text, out ulong hash)
    {
        hash = 0;
        return text is { Length: HexLength } && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash);
    }
}
