using System.Globalization;
using System.Text;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A page in gray as <c>pdftoppm -gray</c> writes it (binary PGM, <c>P5</c>, one byte per pixel): enough to turn it into its negative
/// and to cut a part of it for the OCR, with no image library. Width and height are bounded by the rendering (2400 px at most).
/// </summary>
internal sealed class GrayImage
{
    private const int MaxSide = 10_000;

    private GrayImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>Reads a binary PGM with 8-bit samples; anything else is not a page this reader made (<see cref="InvalidDataException"/>).</summary>
    public static GrayImage Read(byte[] pgm)
    {
        var position = 0;
        string Token()
        {
            while (position < pgm.Length)
            {
                if (pgm[position] == '#')
                    while (position < pgm.Length && pgm[position] != '\n') position++;
                else if (char.IsWhiteSpace((char)pgm[position])) position++;
                else break;
            }
            var start = position;
            while (position < pgm.Length && !char.IsWhiteSpace((char)pgm[position])) position++;
            return Encoding.ASCII.GetString(pgm, start, position - start);
        }

        if (Token() != "P5"
            || !int.TryParse(Token(), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(Token(), NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || Token() != "255"
            || width is < 1 or > MaxSide || height is < 1 or > MaxSide)
            throw new InvalidDataException("Unsupported page image.");
        position++; // the single whitespace after the maximum value
        if (pgm.Length - position < (long)width * height)
            throw new InvalidDataException("Truncated page image.");
        return new GrayImage(width, height, pgm.AsSpan(position, width * height).ToArray());
    }

    public byte[] ToPgm()
    {
        var header = Encoding.ASCII.GetBytes($"P5\n{Width} {Height}\n255\n");
        var bytes = new byte[header.Length + Pixels.Length];
        header.CopyTo(bytes, 0);
        Pixels.CopyTo(bytes, header.Length);
        return bytes;
    }

    /// <summary>White text on a dark band (the title, the header with the code, the box of the class) becomes dark text on white.</summary>
    public GrayImage Negated()
    {
        var pixels = new byte[Pixels.Length];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(255 - Pixels[i]);
        return new GrayImage(Width, Height, pixels);
    }

    /// <summary>The part from (<paramref name="left"/>, <paramref name="top"/>) to (<paramref name="right"/>, <paramref name="bottom"/>), cut to the page; null when nothing is left.</summary>
    public GrayImage? Crop(int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, Width);
        right = Math.Clamp(right, 0, Width);
        top = Math.Clamp(top, 0, Height);
        bottom = Math.Clamp(bottom, 0, Height);
        var width = right - left;
        var height = bottom - top;
        if (width < 8 || height < 8)
            return null;
        var pixels = new byte[width * height];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(Pixels, (top + row) * Width + left, pixels, row * width, width);
        return new GrayImage(width, height, pixels);
    }
}

/// <summary>A word tesseract found, with its box in pixels of the page (a row of its <c>tsv</c> output).</summary>
internal sealed record OcrWord(string Text, int Left, int Top, int Width, int Height)
{
    /// <summary>The words of a tesseract <c>tsv</c> file: <c>level page block par line word left top width height conf text</c>.</summary>
    public static IReadOnlyList<OcrWord> ParseTsv(string tsv)
    {
        var words = new List<OcrWord>();
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var columns = line.TrimEnd('\r').Split('\t');
            if (columns.Length != 12 || string.IsNullOrWhiteSpace(columns[11]))
                continue;
            if (int.TryParse(columns[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left)
                && int.TryParse(columns[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top)
                && int.TryParse(columns[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                && int.TryParse(columns[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
                words.Add(new OcrWord(columns[11].Trim(), left, top, width, height));
        }
        return words;
    }
}

/// <summary>
/// Where to read again, around the labels of the data a contract needs: on a photo the automatic layout of tesseract loses the box of
/// the class or the line of the date among the tables, the same zone cut alone and read as one block gives it back.
/// </summary>
internal static class ApeAnchors
{
    private const int MaxCropsPerPage = 6;

    public static IReadOnlyList<(int Left, int Top, int Right, int Bottom)> Boxes(IEnumerable<OcrWord> words)
    {
        var boxes = new List<(int Left, int Top, int Right, int Bottom)>();
        foreach (var word in words)
        {
            var key = ApeDocumentInspector.Normalize(word.Text).Replace(" ", string.Empty, StringComparison.Ordinal);
            var h = word.Height;
            if (h < 8 || key.Length < 4)
                continue;
            (int, int, int, int)? box =
                key.StartsWith("CLASSE", StringComparison.Ordinal)
                    // The box of the class: the label, the big letter under it, EP gl,nren under that; narrow, so the scale stays out.
                    ? (word.Left - word.Width / 4, word.Top - 4 * h, word.Left + word.Width + word.Width / 4, word.Top + 8 * h)
                : key.StartsWith("EMISSION", StringComparison.Ordinal) || key.StartsWith("VALIDO", StringComparison.Ordinal) || key.StartsWith("FINO", StringComparison.Ordinal)
                    // The line of a date: the label and the date written after it.
                    ? (word.Left - 10 * h, word.Top - h, word.Left + 26 * h, word.Top + 2 * h)
                : key.StartsWith("CODICE", StringComparison.Ordinal) || key.StartsWith("IDENTIFICATIVO", StringComparison.Ordinal)
                    ? (word.Left - h, word.Top - h, word.Left + 40 * h, word.Top + 3 * h)
                : null;
            if (box is not { } b || boxes.Any(other => Overlaps(other, b)))
                continue;
            boxes.Add(b);
            if (boxes.Count == MaxCropsPerPage)
                break;
        }
        return boxes;
    }

    /// <summary>The same zone found twice (in the page and in its negative): it is read once.</summary>
    private static bool Overlaps((int Left, int Top, int Right, int Bottom) a, (int Left, int Top, int Right, int Bottom) b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (width <= 0 || height <= 0)
            return false;
        var smaller = Math.Min((long)(a.Right - a.Left) * (a.Bottom - a.Top), (long)(b.Right - b.Left) * (b.Bottom - b.Top));
        return smaller > 0 && (long)width * height * 10 > smaller * 7;
    }
}
