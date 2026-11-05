using StbImageSharp;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A picture of a guest (a selfie, the photo of a document) held in memory only, 8 bits per channel, red-green-blue (E03/F04/US01, ADR-013-R2).
/// It is decoded from the bytes of the upload with a managed decoder (no native code, no temporary file) and zeroed by <see cref="Dispose"/>, which
/// every caller reaches in a <c>finally</c>. What the caller cannot reach (the temporaries of the decoder and of the models) is freed but not scrubbed.
/// </summary>
public sealed class RasterImage : IDisposable
{
    public RasterImage(int width, int height, byte[] rgb)
    {
        if (width < 1 || height < 1 || rgb.Length != checked(width * height * 3))
            throw new ArgumentException("The pixels do not match the size of the picture.", nameof(rgb));

        Width = width;
        Height = height;
        Rgb = rgb;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row after row, three bytes (red, green, blue) per pixel. Empty after <see cref="Dispose"/>.</summary>
    public byte[] Rgb { get; private set; }

    /// <summary>
    /// Decodes a JPEG or PNG and applies the orientation of its EXIF tag, so the picture is upright. The size is checked before the pixels are
    /// decoded: a small file can hold a huge picture. A picture that cannot be decoded is <see cref="InvalidDataException"/>.
    /// </summary>
    public static RasterImage Decode(byte[] encoded, int maxPixels)
    {
        try
        {
            using (var probe = new MemoryStream(encoded, writable: false))
            {
                var info = ImageInfo.FromStream(probe);
                if (info is null || info.Value.Width < 1 || info.Value.Height < 1)
                    throw new InvalidDataException("Unsupported picture.");
                if ((long)info.Value.Width * info.Value.Height > maxPixels)
                    throw new InvalidDataException("The picture is too large.");
            }

            var decoded = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlue);
            var image = new RasterImage(decoded.Width, decoded.Height, decoded.Data);
            var orientation = ExifOrientation.Read(encoded);
            if (orientation == 1)
                return image;

            using (image)
                return image.Oriented(orientation);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("The picture could not be decoded.");
        }
    }

    /// <summary>A copy at another size: the average of the covered pixels when it shrinks, a linear interpolation when it grows.</summary>
    public RasterImage Resized(int width, int height) =>
        new(width, height, PixelGrid.Resize(Rgb, Width, Height, width, height, 3));

    /// <summary>A copy of the part from (<paramref name="left"/>, <paramref name="top"/>), cut to the picture.</summary>
    public RasterImage Cropped(int left, int top, int width, int height)
    {
        left = Math.Clamp(left, 0, Width - 1);
        top = Math.Clamp(top, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - left);
        height = Math.Clamp(height, 1, Height - top);
        var pixels = new byte[width * height * 3];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(Rgb, ((top + row) * Width + left) * 3, pixels, row * width * 3, width * 3);
        return new RasterImage(width, height, pixels);
    }

    /// <summary>A copy turned as the EXIF orientation says (1 to 8; 6 is a quarter turn clockwise).</summary>
    public RasterImage Oriented(int exifOrientation)
    {
        var pixels = PixelGrid.Orient(Rgb, Width, Height, 3, exifOrientation, out var width, out var height);
        return new RasterImage(width, height, pixels);
    }

    /// <summary>The luminance of every pixel (ITU-R BT.601), as a gray bitmap.</summary>
    public GrayBitmap ToGray()
    {
        var gray = new byte[Width * Height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 3)
            gray[i] = (byte)((Rgb[p] * 299 + Rgb[p + 1] * 587 + Rgb[p + 2] * 114 + 500) / 1000);
        return new GrayBitmap(Width, Height, gray);
    }

    /// <summary>Zeroes the pixels (ADR-013-R2). Safe to call more than once.</summary>
    public void Dispose()
    {
        Array.Clear(Rgb);
        Rgb = [];
    }
}

/// <summary>A picture in 256 grays held in memory only; zeroed by <see cref="Dispose"/>.</summary>
public sealed class GrayBitmap : IDisposable
{
    public GrayBitmap(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1 || pixels.Length != checked(width * height))
            throw new ArgumentException("The pixels do not match the size of the picture.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row after row, one byte per pixel (0 black, 255 white). Empty after <see cref="Dispose"/>.</summary>
    public byte[] Pixels { get; private set; }

    /// <summary>A copy at another size (see <see cref="RasterImage.Resized"/>).</summary>
    public GrayBitmap Resized(int width, int height) =>
        new(width, height, PixelGrid.Resize(Pixels, Width, Height, width, height, 1));

    /// <summary>A copy of the part from (<paramref name="left"/>, <paramref name="top"/>), cut to the picture; null when nothing is left.</summary>
    public GrayBitmap? Cropped(int left, int top, int width, int height)
    {
        var right = Math.Min(Width, left + width);
        var bottom = Math.Min(Height, top + height);
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        if (right - left < 1 || bottom - top < 1)
            return null;

        var pixels = new byte[(right - left) * (bottom - top)];
        for (var row = 0; row < bottom - top; row++)
            Buffer.BlockCopy(Pixels, (top + row) * Width + left, pixels, row * (right - left), right - left);
        return new GrayBitmap(right - left, bottom - top, pixels);
    }

    /// <summary>A copy turned as the EXIF orientation says (6 is a quarter turn clockwise, 8 counter-clockwise, 3 a half turn).</summary>
    public GrayBitmap Oriented(int exifOrientation)
    {
        var pixels = PixelGrid.Orient(Pixels, Width, Height, 1, exifOrientation, out var width, out var height);
        return new GrayBitmap(width, height, pixels);
    }

    /// <summary>
    /// A copy of the same size with the content turned <paramref name="degrees"/> clockwise around the centre (a small angle: the borders are
    /// filled with white), bilinear.
    /// </summary>
    public GrayBitmap Rotated(double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var cx = (Width - 1) / 2.0;
        var cy = (Height - 1) / 2.0;
        var pixels = new byte[Pixels.Length];
        for (var y = 0; y < Height; y++)
        {
            var dy = y - cy;
            for (var x = 0; x < Width; x++)
            {
                var dx = x - cx;
                var sx = cx + cos * dx + sin * dy;
                var sy = cy - sin * dx + cos * dy;
                pixels[y * Width + x] = Sample(sx, sy);
            }
        }

        return new GrayBitmap(Width, Height, pixels);
    }

    /// <summary>Zeroes the pixels (ADR-013-R2). Safe to call more than once.</summary>
    public void Dispose()
    {
        Array.Clear(Pixels);
        Pixels = [];
    }

    private byte Sample(double x, double y)
    {
        if (x < 0 || y < 0 || x > Width - 1 || y > Height - 1)
            return 255;

        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = x - x0;
        var fy = y - y0;
        var top = Pixels[y0 * Width + x0] * (1 - fx) + Pixels[y0 * Width + x1] * fx;
        var bottom = Pixels[y1 * Width + x0] * (1 - fx) + Pixels[y1 * Width + x1] * fx;
        return (byte)(top * (1 - fy) + bottom * fy + 0.5);
    }
}

/// <summary>Resampling and turning of interleaved 8-bit pixel grids, shared by the colour and the gray pictures.</summary>
internal static class PixelGrid
{
    /// <summary>A grid at another size: area average when it shrinks, linear interpolation when it grows, one separable pass per axis.</summary>
    public static byte[] Resize(byte[] source, int sourceWidth, int sourceHeight, int width, int height, int channels)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (width == sourceWidth && height == sourceHeight)
            return (byte[])source.Clone();

        var columns = BuildAxis(sourceWidth, width);
        var rows = BuildAxis(sourceHeight, height);
        var horizontal = new float[sourceHeight * width * channels];
        try
        {
            for (var y = 0; y < sourceHeight; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (first, weights) = columns[x];
                    for (var c = 0; c < channels; c++)
                    {
                        float sum = 0;
                        for (var k = 0; k < weights.Length; k++)
                            sum += weights[k] * source[((y * sourceWidth) + first + k) * channels + c];
                        horizontal[((y * width) + x) * channels + c] = sum;
                    }
                }
            }

            var result = new byte[width * height * channels];
            for (var y = 0; y < height; y++)
            {
                var (first, weights) = rows[y];
                for (var x = 0; x < width; x++)
                {
                    for (var c = 0; c < channels; c++)
                    {
                        float sum = 0;
                        for (var k = 0; k < weights.Length; k++)
                            sum += weights[k] * horizontal[(((first + k) * width) + x) * channels + c];
                        result[((y * width) + x) * channels + c] = (byte)Math.Clamp((int)(sum + 0.5f), 0, 255);
                    }
                }
            }

            return result;
        }
        finally
        {
            Array.Clear(horizontal);
        }
    }

    /// <summary>
    /// Turns a grid as the EXIF orientation says: 2 mirror, 3 half turn, 4 flip, 5 transpose, 6 quarter turn clockwise, 7 transverse,
    /// 8 quarter turn counter-clockwise; anything else is a copy.
    /// </summary>
    public static byte[] Orient(byte[] source, int sourceWidth, int sourceHeight, int channels, int orientation, out int width, out int height)
    {
        var swap = orientation is >= 5 and <= 8;
        width = swap ? sourceHeight : sourceWidth;
        height = swap ? sourceWidth : sourceHeight;
        if (orientation is < 2 or > 8)
            return (byte[])source.Clone();

        var result = new byte[width * height * channels];
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < sourceWidth; x++)
            {
                int nx, ny;
                switch (orientation)
                {
                    case 2:
                        nx = sourceWidth - 1 - x;
                        ny = y;
                        break;
                    case 3:
                        nx = sourceWidth - 1 - x;
                        ny = sourceHeight - 1 - y;
                        break;
                    case 4:
                        nx = x;
                        ny = sourceHeight - 1 - y;
                        break;
                    case 5:
                        nx = y;
                        ny = x;
                        break;
                    case 6:
                        nx = sourceHeight - 1 - y;
                        ny = x;
                        break;
                    case 7:
                        nx = sourceHeight - 1 - y;
                        ny = sourceWidth - 1 - x;
                        break;
                    default:
                        nx = y;
                        ny = sourceWidth - 1 - x;
                        break;
                }

                var from = (y * sourceWidth + x) * channels;
                var to = (ny * width + nx) * channels;
                for (var c = 0; c < channels; c++)
                    result[to + c] = source[from + c];
            }
        }

        return result;
    }

    /// <summary>For every pixel of the result, the first source pixel it reads and the weights of the ones from there on.</summary>
    private static (int First, float[] Weights)[] BuildAxis(int sourceSize, int size)
    {
        var axis = new (int First, float[] Weights)[size];
        var scale = (double)sourceSize / size;
        for (var i = 0; i < size; i++)
        {
            if (scale > 1)
            {
                var start = i * scale;
                var end = start + scale;
                var first = Math.Min((int)Math.Floor(start), sourceSize - 1);
                var last = Math.Max(first, Math.Min(sourceSize - 1, (int)Math.Ceiling(end) - 1));
                var weights = new float[last - first + 1];
                double total = 0;
                for (var j = first; j <= last; j++)
                {
                    var overlap = Math.Max(0, Math.Min(end, j + 1) - Math.Max(start, j));
                    weights[j - first] = (float)overlap;
                    total += overlap;
                }

                if (total <= 0)
                {
                    weights[0] = 1;
                    total = 1;
                }

                for (var k = 0; k < weights.Length; k++)
                    weights[k] = (float)(weights[k] / total);
                axis[i] = (first, weights);
            }
            else
            {
                var centre = (i + 0.5) * scale - 0.5;
                var first = (int)Math.Floor(centre);
                if (first < 0)
                    axis[i] = (0, [1f]);
                else if (first >= sourceSize - 1)
                    axis[i] = (sourceSize - 1, [1f]);
                else
                    axis[i] = (first, [(float)(1 - (centre - first)), (float)(centre - first)]);
            }
        }

        return axis;
    }
}

/// <summary>Reads the orientation tag (0x0112) that cameras write into the EXIF block of a JPEG.</summary>
internal static class ExifOrientation
{
    /// <summary>The orientation 1 to 8 of a JPEG; 1 (upright) when the bytes are not a JPEG, carry no tag or are damaged.</summary>
    public static int Read(byte[] data)
    {
        if (data.Length < 12 || data[0] != 0xFF || data[1] != 0xD8)
            return 1;

        var position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
                return 1;

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            // Markers without a length: the start of the image, the restart markers and the temporary one.
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                position += 2;
                continue;
            }

            // The scan or the end of the image: no more headers.
            if (marker == 0xDA || marker == 0xD9)
                return 1;

            var length = (data[position + 2] << 8) | data[position + 3];
            if (length < 2 || position + 2 + length > data.Length)
                return 1;

            if (marker == 0xE1 && length >= 8 && data[position + 4] == (byte)'E' && data[position + 5] == (byte)'x'
                && data[position + 6] == (byte)'i' && data[position + 7] == (byte)'f' && data[position + 8] == 0 && data[position + 9] == 0)
                return ReadTiff(data, position + 10, position + 2 + length);

            position += 2 + length;
        }

        return 1;
    }

    private static int ReadTiff(byte[] data, int tiff, int end)
    {
        if (end - tiff < 8)
            return 1;

        var little = data[tiff] == (byte)'I' && data[tiff + 1] == (byte)'I';
        var big = data[tiff] == (byte)'M' && data[tiff + 1] == (byte)'M';
        if (!little && !big)
            return 1;

        int U16(int at) => little ? data[at] | (data[at + 1] << 8) : (data[at] << 8) | data[at + 1];

        if (U16(tiff + 2) != 42)
            return 1;

        long offset = little
            ? (long)data[tiff + 4] | ((long)data[tiff + 5] << 8) | ((long)data[tiff + 6] << 16) | ((long)data[tiff + 7] << 24)
            : ((long)data[tiff + 4] << 24) | ((long)data[tiff + 5] << 16) | ((long)data[tiff + 6] << 8) | (long)data[tiff + 7];
        var directory = tiff + offset;
        if (offset < 8 || directory + 2 > end)
            return 1;

        var count = U16((int)directory);
        for (var n = 0; n < count; n++)
        {
            var entry = (int)directory + 2 + (n * 12);
            if (entry + 12 > end)
                return 1;

            if (U16(entry) == 0x0112)
            {
                var value = U16(entry + 8);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }

        return 1;
    }
}
