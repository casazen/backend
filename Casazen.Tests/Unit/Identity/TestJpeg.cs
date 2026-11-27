namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// A baseline JPEG encoder for the tests of the presence (E72/F01/US01): the frames the browser sends are JPEG, and the tests of the intake need bytes that the real
/// decoder of the application (StbImageSharp) reads as pictures of a size and pixels the test knows. One gray component, 8 x 8 blocks, a flat quantisation table and
/// a Huffman table generated here (every symbol the coefficients can need gets a code of the same length), so no table of the standard has to be remembered and the
/// result is still a file every decoder reads. Generated in code: no photograph of anybody is in the repository, which is public.
/// </summary>
public static class TestJpeg
{
    /// <summary>The step every coefficient is divided by: small enough to keep the pixels close to the ones given, large enough to keep the file short.</summary>
    private const int Quantiser = 4;

    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>The cosines of the transform: sample (or row) <c>x</c> against frequency <c>u</c>.</summary>
    private static readonly double[,] Cosines = BuildCosines();

    /// <summary>The 12 categories of a DC difference, each a code of 4 bits (codes 0 to 11; the all-ones code is never used).</summary>
    private static readonly byte[] DcSymbols = [.. Enumerable.Range(0, 12).Select(symbol => (byte)symbol)];

    /// <summary>
    /// Every symbol an AC coefficient can need: the end of the block, a run of 16 zeros, and a run of 0 to 15 zeros before a value of 1 to 10 bits. 162 codes of 8 bits
    /// (codes 0 to 161: the all-ones code is never used).
    /// </summary>
    private static readonly byte[] AcSymbols =
    [
        0x00,
        0xF0,
        .. Enumerable.Range(0, 16).SelectMany(run => Enumerable.Range(1, 10).Select(size => (byte)((run << 4) | size))),
    ];

    /// <summary>A JPEG of <paramref name="pixels"/> (one gray byte per pixel, row after row).</summary>
    public static byte[] Gray(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1 || pixels.Length != width * height)
            throw new ArgumentException("The pixels do not match the size of the picture.", nameof(pixels));

        using var file = new MemoryStream();
        file.Write([0xFF, 0xD8]);

        // The quantisation table: precision 0, number 0, 64 steps.
        Segment(file, 0xDB, [0x00, .. Enumerable.Repeat((byte)Quantiser, 64)]);

        // The frame: 8 bits, height, width, one component (number 1, no subsampling, table 0).
        Segment(file, 0xC0, [0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00]);

        // The two Huffman tables: DC number 0 (12 codes of 4 bits) and AC number 0 (162 codes of 8 bits).
        Segment(file, 0xC4, [0x00, .. Counts(4, DcSymbols.Length), .. DcSymbols]);
        Segment(file, 0xC4, [0x10, .. Counts(8, AcSymbols.Length), .. AcSymbols]);

        // The scan: one component, DC and AC tables 0, the whole spectrum.
        Segment(file, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        var dc = Codes(DcSymbols, 4);
        var ac = Codes(AcSymbols, 8);
        var bits = new BitWriter(file);
        var previousDc = 0;
        var block = new double[64];
        var coefficients = new int[64];
        for (var blockTop = 0; blockTop < height; blockTop += 8)
        {
            for (var blockLeft = 0; blockLeft < width; blockLeft += 8)
            {
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        // The edges are repeated where a block goes over the picture.
                        var sampleX = Math.Min(blockLeft + x, width - 1);
                        var sampleY = Math.Min(blockTop + y, height - 1);
                        block[(y * 8) + x] = pixels[(sampleY * width) + sampleX] - 128;
                    }
                }

                Transform(block, coefficients);
                previousDc = WriteBlock(bits, coefficients, previousDc, dc, ac);
            }
        }

        bits.Flush();
        file.Write([0xFF, 0xD9]);
        return file.ToArray();
    }

    /// <summary>A JPEG of a flat gray picture.</summary>
    public static byte[] Flat(int width, int height, byte gray = 128)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, gray);
        return Gray(width, height, pixels);
    }

    /// <summary>A JPEG of random gray noise, the same for the same <paramref name="seed"/>.</summary>
    public static byte[] Noise(int width, int height, int seed)
    {
        return Gray(width, height, NoisePixels(width, height, seed));
    }

    /// <summary>The gray pixels of <see cref="Noise"/>: random bytes, the same for the same <paramref name="seed"/>.</summary>
    public static byte[] NoisePixels(int width, int height, int seed)
    {
        var pixels = new byte[width * height];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    private static double[,] BuildCosines()
    {
        var table = new double[8, 8];
        for (var x = 0; x < 8; x++)
        {
            for (var u = 0; u < 8; u++)
                table[x, u] = Math.Cos((((2 * x) + 1) * u * Math.PI) / 16);
        }

        return table;
    }

    private static void Segment(Stream file, byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        file.Write([0xFF, marker, (byte)(length >> 8), (byte)length]);
        file.Write(payload);
    }

    /// <summary>The 16 counts of a Huffman table in which every code has <paramref name="length"/> bits.</summary>
    private static byte[] Counts(int length, int symbols)
    {
        var counts = new byte[16];
        counts[length - 1] = (byte)symbols;
        return counts;
    }

    /// <summary>The code of every symbol of a table of <paramref name="length"/>-bit codes assigned in the order the symbols are listed.</summary>
    private static (int Code, int Length)[] Codes(byte[] symbols, int length)
    {
        var codes = new (int Code, int Length)[256];
        for (var index = 0; index < symbols.Length; index++)
            codes[symbols[index]] = (index, length);

        return codes;
    }

    /// <summary>The discrete cosine transform of an 8 x 8 block of levels, quantised, in the zigzag order of the file (one pass over the rows, one over the columns).</summary>
    private static void Transform(double[] block, int[] coefficients)
    {
        var rows = new double[64];
        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                double sum = 0;
                for (var x = 0; x < 8; x++)
                    sum += block[(y * 8) + x] * Cosines[x, u];

                rows[(y * 8) + u] = sum;
            }
        }

        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                double sum = 0;
                for (var y = 0; y < 8; y++)
                    sum += rows[(y * 8) + u] * Cosines[y, v];

                var scale = (u == 0 ? 1 / Math.Sqrt(2) : 1) * (v == 0 ? 1 / Math.Sqrt(2) : 1) / 4;
                coefficients[Array.IndexOf(ZigZag, (v * 8) + u)] = (int)Math.Round(scale * sum / Quantiser);
            }
        }
    }

    /// <summary>One block: the difference of its DC with the one before, then its AC values with the runs of zeros between them. The DC of the block, for the next.</summary>
    private static int WriteBlock(BitWriter bits, int[] coefficients, int previousDc, (int Code, int Length)[] dc, (int Code, int Length)[] ac)
    {
        var difference = coefficients[0] - previousDc;
        var category = Category(difference);
        bits.Write(dc[category].Code, dc[category].Length);
        bits.Write(Magnitude(difference, category), category);

        var run = 0;
        for (var index = 1; index < 64; index++)
        {
            if (coefficients[index] == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                bits.Write(ac[0xF0].Code, ac[0xF0].Length);
                run -= 16;
            }

            var size = Category(coefficients[index]);
            var symbol = (run << 4) | size;
            bits.Write(ac[symbol].Code, ac[symbol].Length);
            bits.Write(Magnitude(coefficients[index], size), size);
            run = 0;
        }

        if (run > 0)
            bits.Write(ac[0x00].Code, ac[0x00].Length);

        return coefficients[0];
    }

    /// <summary>The number of bits of the magnitude of a value (0 for 0).</summary>
    private static int Category(int value)
    {
        var magnitude = Math.Abs(value);
        var category = 0;
        while (magnitude > 0)
        {
            category++;
            magnitude >>= 1;
        }

        return category;
    }

    /// <summary>The bits that follow a category: the value itself when it is positive, its one's complement in <paramref name="category"/> bits when it is negative.</summary>
    private static int Magnitude(int value, int category)
    {
        return value >= 0 ? value : value + ((1 << category) - 1);
    }

    /// <summary>Writes bits to the scan, most significant first, with a zero byte after every FF byte, and pads the last byte with ones.</summary>
    private sealed class BitWriter(Stream output)
    {
        private int _buffer;
        private int _count;

        public void Write(int value, int length)
        {
            for (var bit = length - 1; bit >= 0; bit--)
            {
                _buffer = (_buffer << 1) | ((value >> bit) & 1);
                if (++_count == 8)
                    Emit();
            }
        }

        public void Flush()
        {
            while (_count != 0)
                Write(1, 1);
        }

        private void Emit()
        {
            output.WriteByte((byte)_buffer);
            if (_buffer == 0xFF)
                output.WriteByte(0x00);

            _buffer = 0;
            _count = 0;
        }
    }
}
