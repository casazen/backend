using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit.Identity;

internal static class DocumentImageFactory
{
    public const int Width = 640;
    public const int Height = 480;

    private const byte BackgroundLevel = 70;
    private const byte CardLevel = 235;
    private const byte BorderLevel = 150;
    private const byte InkLevel = 25;
    private const int BorderThickness = 4;

    private static readonly (byte R, byte G, byte B) Skin = (200, 150, 120);
    private static readonly (byte R, byte G, byte B) Hair = (40, 30, 30);

    public readonly record struct Placement(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width;

        public int Bottom => Top + Height;
    }

    public static Placement WholeCard { get; } = new(80, 70, 480, 303);

    public static RasterImage Render(
        DocumentFormat format, DocumentSide side, Placement card, bool withFace = true, double glareShare = 0, int imageWidth = Width, int imageHeight = Height)
    {
        var rgb = new byte[imageWidth * imageHeight * 3];
        var canvas = new Canvas(rgb, imageWidth, imageHeight);
        canvas.Fill(0, 0, imageWidth, imageHeight, BackgroundLevel, BackgroundLevel, BackgroundLevel);
        canvas.Fill(card.Left, card.Top, card.Width, card.Height, BorderLevel, BorderLevel, BorderLevel);
        canvas.Fill(
            card.Left + BorderThickness, card.Top + BorderThickness, card.Width - (2 * BorderThickness), card.Height - (2 * BorderThickness), CardLevel, CardLevel, CardLevel);

        var layout = format.Layout.Sides[DocumentSides.CodeOf(side)];
        foreach (var field in layout.Fields)
            TextLine(canvas, card, field.Zone);

        if (layout.Portrait is { } portrait && withFace)
            FaceStandIn(canvas, card, portrait);

        if (layout.MrzRegion is { } mrz)
            MrzBand(canvas, card, mrz);

        if (glareShare > 0)
        {
            var scale = Math.Sqrt(glareShare);
            var patchWidth = (int)(card.Width * scale);
            var patchHeight = (int)(card.Height * scale);
            canvas.Fill(card.Right - BorderThickness - patchWidth - 4, card.Bottom - BorderThickness - patchHeight - 4, patchWidth, patchHeight, 255, 255, 255);
        }

        return new RasterImage(imageWidth, imageHeight, rgb);
    }

    public static RasterImage Plain(byte level, int width = Width, int height = Height)
    {
        var rgb = new byte[width * height * 3];
        Array.Fill(rgb, level);
        return new RasterImage(width, height, rgb);
    }

    public static RasterImage TextPage(int lines = 22, int imageWidth = Width, int imageHeight = Height)
    {
        var rgb = new byte[imageWidth * imageHeight * 3];
        var canvas = new Canvas(rgb, imageWidth, imageHeight);
        canvas.Fill(0, 0, imageWidth, imageHeight, CardLevel, CardLevel, CardLevel);
        var pitch = imageHeight / (lines + 1);
        for (var line = 1; line <= lines; line++)
        {
            for (var x = 24; x + 9 <= imageWidth - 24; x += 16)
                canvas.Fill(x, (line * pitch) - 4, 9, 8, InkLevel, InkLevel, InkLevel);
        }

        return new RasterImage(imageWidth, imageHeight, rgb);
    }

    public static RasterImage LowContrast(RasterImage source, byte low, byte high)
    {
        var rgb = new byte[source.Rgb.Length];
        for (var i = 0; i < rgb.Length; i++)
            rgb[i] = (byte)(low + (((source.Rgb[i] * (high - low)) + 127) / 255));

        return new RasterImage(source.Width, source.Height, rgb);
    }

    public static RasterImage TurnedClockwise(RasterImage source) =>
        Remap(source, source.Height, source.Width, (x, y) => (source.Height - 1 - y, x));

    public static RasterImage TurnedCounterClockwise(RasterImage source) =>
        Remap(source, source.Height, source.Width, (x, y) => (y, source.Width - 1 - x));

    public static RasterImage TurnedHalf(RasterImage source) =>
        Remap(source, source.Width, source.Height, (x, y) => (source.Width - 1 - x, source.Height - 1 - y));

    public static RasterImage Rotated(RasterImage source, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var centreX = (source.Width - 1) / 2.0;
        var centreY = (source.Height - 1) / 2.0;
        var rgb = new byte[source.Rgb.Length];
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var dx = x - centreX;
                var dy = y - centreY;
                var fromX = centreX + (cos * dx) + (sin * dy);
                var fromY = centreY - (sin * dx) + (cos * dy);
                var at = ((y * source.Width) + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                    rgb[at + channel] = Sample(source, fromX, fromY, channel);
            }
        }

        return new RasterImage(source.Width, source.Height, rgb);
    }

    public static RasterImage Brightened(RasterImage source, int delta)
    {
        var rgb = new byte[source.Rgb.Length];
        for (var i = 0; i < rgb.Length; i++)
            rgb[i] = (byte)Math.Clamp(source.Rgb[i] + delta, 0, 255);

        return new RasterImage(source.Width, source.Height, rgb);
    }

    public static RasterImage Blurred(RasterImage source, int radius, int passes)
    {
        var width = source.Width;
        var height = source.Height;
        var current = (byte[])source.Rgb.Clone();
        var next = new byte[current.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            Blur(current, next, width, height, radius, horizontal: true);
            Blur(next, current, width, height, radius, horizontal: false);
        }

        return new RasterImage(width, height, current);
    }

    private static void TextLine(Canvas canvas, Placement card, DocumentFormatRegion zone)
    {
        var left = card.Left + (int)Math.Round(zone.X * card.Width);
        var right = left + (int)Math.Round(zone.W * card.Width);
        var top = card.Top + (int)Math.Round((zone.Y + (zone.H * 0.3)) * card.Height);
        var height = Math.Max(3, (int)Math.Round(zone.H * 0.4 * card.Height));
        for (var x = left; x + 9 <= right; x += 16)
            canvas.Fill(x, top, 9, height, InkLevel, InkLevel, InkLevel);
    }

    private static void MrzBand(Canvas canvas, Placement card, DocumentFormatRegion mrz)
    {
        var left = card.Left + (int)Math.Round(mrz.X * card.Width);
        var right = left + (int)Math.Round(mrz.W * card.Width);
        var top = card.Top + (int)Math.Round(mrz.Y * card.Height);
        var bandHeight = (int)Math.Round(mrz.H * card.Height);
        var lineHeight = (int)Math.Round(bandHeight * 0.2);
        var gap = (int)Math.Round(bandHeight * 0.08);
        for (var line = 0; line < 3; line++)
        {
            var lineTop = top + gap + (line * (lineHeight + gap));
            for (var x = left; x + 5 <= right; x += 8)
                canvas.Fill(x, lineTop, 5, lineHeight, InkLevel, InkLevel, InkLevel);
        }
    }

    /// <summary>
    /// What stands for the portrait: a box of skin colour filling the zone, with a dark bar (the hair, or the eyes) in its upper part, so that it can be told upright from
    /// turned. No face is drawn: the detector of the tests (<see cref="PortraitStandInDetector"/>) knows this one box.
    /// </summary>
    private static void FaceStandIn(Canvas canvas, Placement card, DocumentFormatRegion zone)
    {
        var left = card.Left + (int)Math.Round(zone.X * card.Width);
        var top = card.Top + (int)Math.Round(zone.Y * card.Height);
        var width = (int)Math.Round(zone.W * card.Width);
        var height = (int)Math.Round(zone.H * card.Height);
        canvas.Fill(left, top, width, height, Skin.R, Skin.G, Skin.B);
        canvas.Fill(left + (int)(width * 0.15), top + (int)(height * 0.18), (int)(width * 0.7), (int)(height * 0.12), Hair.R, Hair.G, Hair.B);
    }

    internal static bool IsSkin(byte[] rgb, int at) => Near(rgb, at, Skin);

    internal static bool IsHair(byte[] rgb, int at) => Near(rgb, at, Hair);

    private static bool Near(byte[] rgb, int at, (byte R, byte G, byte B) colour) =>
        Math.Abs(rgb[at] - colour.R) <= 20 && Math.Abs(rgb[at + 1] - colour.G) <= 20 && Math.Abs(rgb[at + 2] - colour.B) <= 20;

    private static byte Sample(RasterImage source, double x, double y, int channel)
    {
        if (x < 0 || y < 0 || x > source.Width - 1 || y > source.Height - 1)
            return BackgroundLevel;

        var left = (int)x;
        var top = (int)y;
        var right = Math.Min(left + 1, source.Width - 1);
        var bottom = Math.Min(top + 1, source.Height - 1);
        var shareX = x - left;
        var shareY = y - top;
        double At(int column, int row) => source.Rgb[(((row * source.Width) + column) * 3) + channel];
        var upper = (At(left, top) * (1 - shareX)) + (At(right, top) * shareX);
        var lower = (At(left, bottom) * (1 - shareX)) + (At(right, bottom) * shareX);
        return (byte)((upper * (1 - shareY)) + (lower * shareY) + 0.5);
    }

    private static RasterImage Remap(RasterImage source, int width, int height, Func<int, int, (int X, int Y)> destination)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var (nx, ny) = destination(x, y);
                Buffer.BlockCopy(source.Rgb, ((y * source.Width) + x) * 3, rgb, ((ny * width) + nx) * 3, 3);
            }
        }

        return new RasterImage(width, height, rgb);
    }

    private static void Blur(byte[] from, byte[] to, int width, int height, int radius, bool horizontal)
    {
        var lines = horizontal ? height : width;
        var length = horizontal ? width : height;
        var span = (2 * radius) + 1;
        for (var line = 0; line < lines; line++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                int Index(int position) => horizontal
                    ? (((line * width) + Math.Clamp(position, 0, length - 1)) * 3) + channel
                    : (((Math.Clamp(position, 0, length - 1) * width) + line) * 3) + channel;

                var sum = 0;
                for (var k = -radius; k <= radius; k++)
                    sum += from[Index(k)];

                for (var position = 0; position < length; position++)
                {
                    to[Index(position)] = (byte)((sum + (span / 2)) / span);
                    sum += from[Index(position + radius + 1)] - from[Index(position - radius)];
                }
            }
        }
    }

    private sealed class Canvas(byte[] rgb, int width, int height)
    {
        public void Fill(int left, int top, int rectangleWidth, int rectangleHeight, byte r, byte g, byte b)
        {
            for (var y = Math.Max(0, top); y < Math.Min(height, top + rectangleHeight); y++)
            {
                for (var x = Math.Max(0, left); x < Math.Min(width, left + rectangleWidth); x++)
                {
                    var at = ((y * width) + x) * 3;
                    rgb[at] = r;
                    rgb[at + 1] = g;
                    rgb[at + 2] = b;
                }
            }
        }
    }
}

internal sealed class PortraitStandInDetector : IFaceDetector
{
    public int Calls { get; private set; }

    public List<byte[]> SeenPixels { get; } = [];

    public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
    {
        Calls++;
        var rgb = image.Rgb;
        SeenPixels.Add(rgb);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, skin = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (!DocumentImageFactory.IsSkin(rgb, ((y * image.Width) + x) * 3))
                    continue;

                skin++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (skin < 200)
            return [];

        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        long sumX = 0;
        long sumY = 0;
        var hair = 0;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (!DocumentImageFactory.IsHair(rgb, ((y * image.Width) + x) * 3))
                    continue;

                hair++;
                sumX += x;
                sumY += y;
            }
        }

        if (hair == 0)
            return [];

        var upright = ((double)sumY / hair) < minY + (0.4 * height) && Math.Abs(((double)sumX / hair) - (minX + (width / 2.0))) < 0.1 * width;
        if (!upright)
            return [];

        return [new DetectedFace(minX, minY, width, height, 0.95f, new float[10], (double)(width * height) / (image.Width * image.Height))];
    }
}
