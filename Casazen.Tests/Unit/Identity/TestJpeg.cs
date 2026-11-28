namespace Casazen.Tests.Unit.Identity;

public static class TestJpeg
{
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

    private static readonly double[,] Cosines = BuildCosines();

    private static readonly byte[] DcSymbols = [.. Enumerable.Range(0, 12).Select(symbol => (byte)symbol)];

    private static readonly byte[] AcSymbols =
    [
        0x00,
        0xF0,
        .. Enumerable.Range(0, 16).SelectMany(run => Enumerable.Range(1, 10).Select(size => (byte)((run << 4) | size))),
    ];

    public static byte[] Gray(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1 || pixels.Length != width * height)
            throw new ArgumentException("The pixels do not match the size of the picture.", nameof(pixels));

        using var file = new MemoryStream();
        file.Write([0xFF, 0xD8]);

        Segment(file, 0xDB, [0x00, .. Enumerable.Repeat((byte)Quantiser, 64)]);

        Segment(file, 0xC0, [0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00]);

        Segment(file, 0xC4, [0x00, .. Counts(4, DcSymbols.Length), .. DcSymbols]);
        Segment(file, 0xC4, [0x10, .. Counts(8, AcSymbols.Length), .. AcSymbols]);

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

    public static byte[] Flat(int width, int height, byte gray = 128)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, gray);
        return Gray(width, height, pixels);
    }

    public static byte[] Noise(int width, int height, int seed)
    {
        return Gray(width, height, NoisePixels(width, height, seed));
    }

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

    private static byte[] Counts(int length, int symbols)
    {
        var counts = new byte[16];
        counts[length - 1] = (byte)symbols;
        return counts;
    }

    private static (int Code, int Length)[] Codes(byte[] symbols, int length)
    {
        var codes = new (int Code, int Length)[256];
        for (var index = 0; index < symbols.Length; index++)
            codes[symbols[index]] = (index, length);

        return codes;
    }

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

    private static int Magnitude(int value, int category)
    {
        return value >= 0 ? value : value + ((1 << category) - 1);
    }

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
