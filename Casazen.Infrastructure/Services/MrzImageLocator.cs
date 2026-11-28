using Casazen.Core.Options;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public sealed class MrzLine : IDisposable
{
    public MrzLine(GrayBitmap gray, GrayBitmap binary, int top, int bottom, int left, int right, double glareShare, double blurRatio)
    {
        Gray = gray;
        Binary = binary;
        Top = top;
        Bottom = bottom;
        Left = left;
        Right = right;
        GlareShare = glareShare;
        BlurRatio = blurRatio;
    }

    public GrayBitmap Gray { get; }

    public GrayBitmap Binary { get; }

    public int Top { get; }

    public int Bottom { get; }

    public int Left { get; }

    public int Right { get; }

    public double GlareShare { get; }

    public double BlurRatio { get; }

    public void Dispose()
    {
        Gray.Dispose();
        Binary.Dispose();
    }
}

public sealed class MrzLocation : IDisposable
{
    public static MrzLocation Empty { get; } = new([], []);

    public MrzLocation(IReadOnlyList<MrzLine> lines, IReadOnlyList<IReadOnlyList<int>> groups)
    {
        Lines = lines;
        Groups = groups;
    }

    public IReadOnlyList<MrzLine> Lines { get; }

    public IReadOnlyList<IReadOnlyList<int>> Groups { get; }

    public void Dispose()
    {
        foreach (var line in Lines)
            line.Dispose();
    }
}

public interface IMrzBandLocator
{
    MrzLocation Locate(GrayBitmap page, CancellationToken cancellationToken);
}

public sealed class MrzImageLocator(IOptions<MrzOcrOptions>? options = null) : IMrzBandLocator
{
    private const double SearchStart = 0.30;

    private const double MinAspect = 12;

    private const double TargetLineHeight = 40;

    public MrzLocation Locate(GrayBitmap page, CancellationToken cancellationToken)
    {
        var settings = options?.Value ?? new MrzOcrOptions();
        if (page.Width < 200 || page.Height < 100)
            return MrzLocation.Empty;

        GrayBitmap? working = null;
        GrayBitmap? levelled = null;
        bool[]? ink = null;
        try
        {
            var longest = Math.Max(page.Width, page.Height);
            working = longest > settings.MaxWorkingSide
                ? page.Resized(
                    Math.Max(1, (int)Math.Round((double)page.Width * settings.MaxWorkingSide / longest)),
                    Math.Max(1, (int)Math.Round((double)page.Height * settings.MaxWorkingSide / longest)))
                : null;
            var source = working ?? page;

            cancellationToken.ThrowIfCancellationRequested();
            var tilt = EstimateTiltDegrees(source);
            levelled = Math.Abs(tilt) < 0.25 ? null : source.Rotated(-tilt);
            var picture = levelled ?? source;

            cancellationToken.ThrowIfCancellationRequested();
            ink = InkMask(picture, Math.Clamp(picture.Width / 32, 8, 80), 0.85);
            var runs = FindTextRuns(picture, ink);
            if (runs.Count == 0)
                return MrzLocation.Empty;

            var lines = new List<MrzLine>();
            var geometry = new List<Run>();
            foreach (var run in runs)
            {
                var line = CutLine(picture, ink, run);
                if (line is null)
                    continue;

                lines.Add(line);
                geometry.Add(run with { Left = line.Left, Right = line.Right });
            }

            var groups = GroupLines(geometry, picture.Width, picture.Height);
            if (groups.Count == 0)
            {
                foreach (var line in lines)
                    line.Dispose();

                return MrzLocation.Empty;
            }

            return new MrzLocation(lines, groups);
        }
        finally
        {
            if (ink is not null)
                Array.Clear(ink);

            levelled?.Dispose();
            working?.Dispose();
        }
    }

    internal sealed record Run(int Top, int Bottom, int Left, int Right)
    {
        public int Height => Bottom - Top + 1;

        public int Width => Right - Left + 1;
    }

    internal static double EstimateTiltDegrees(GrayBitmap page)
    {
        GrayBitmap? reduced = null;
        bool[]? mask = null;
        try
        {
            var scale = Math.Min(1.0, 700.0 / page.Width);
            if (scale < 1)
                reduced = page.Resized(Math.Max(16, (int)(page.Width * scale)), Math.Max(16, (int)(page.Height * scale)));

            var picture = reduced ?? page;
            mask = InkMask(picture, Math.Max(4, picture.Width / 40), 0.85);
            var points = new List<(int X, int Y)>();
            for (var y = (int)(picture.Height * SearchStart); y < picture.Height; y++)
            {
                for (var x = 0; x < picture.Width; x++)
                {
                    if (mask[(y * picture.Width) + x])
                        points.Add((x, y));
                }
            }

            if (points.Count < 200)
                return 0;

            var offset = (picture.Width / 2) + 2;
            var bins = new int[picture.Height + picture.Width + 4];
            var centre = picture.Width / 2.0;

            long ScoreOf(double degrees)
            {
                var slope = Math.Tan(degrees * Math.PI / 180.0);
                Array.Clear(bins);
                foreach (var (x, y) in points)
                {
                    var row = (int)Math.Round(y - ((x - centre) * slope)) + offset;
                    if ((uint)row < (uint)bins.Length)
                        bins[row]++;
                }

                long score = 0;
                foreach (var count in bins)
                    score += (long)count * count;

                return score;
            }

            var zeroScore = ScoreOf(0);
            double best = 0;
            var bestScore = zeroScore;
            for (var step = -20; step <= 20; step++)
            {
                var score = ScoreOf(step * 0.5);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = step * 0.5;
                }
            }

            if (bestScore <= zeroScore * 1.03)
                return 0;

            var coarse = best;
            for (var step = -4; step <= 4; step++)
            {
                var score = ScoreOf(coarse + (step * 0.1));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = coarse + (step * 0.1);
                }
            }

            return best;
        }
        finally
        {
            if (mask is not null)
                Array.Clear(mask);

            reduced?.Dispose();
        }
    }

    internal static bool[] InkMask(GrayBitmap picture, int radius, double ratio)
    {
        var width = picture.Width;
        var height = picture.Height;
        var stride = width + 1;
        var integral = new int[stride * (height + 1)];
        try
        {
            for (var y = 0; y < height; y++)
            {
                var rowSum = 0;
                for (var x = 0; x < width; x++)
                {
                    rowSum += picture.Pixels[(y * width) + x];
                    integral[((y + 1) * stride) + x + 1] = integral[(y * stride) + x + 1] + rowSum;
                }
            }

            var mask = new bool[width * height];
            for (var y = 0; y < height; y++)
            {
                var top = Math.Max(0, y - radius);
                var bottom = Math.Min(height - 1, y + radius);
                for (var x = 0; x < width; x++)
                {
                    var left = Math.Max(0, x - radius);
                    var right = Math.Min(width - 1, x + radius);
                    var count = (right - left + 1) * (bottom - top + 1);
                    var sum = integral[((bottom + 1) * stride) + right + 1] - integral[(top * stride) + right + 1]
                        - integral[((bottom + 1) * stride) + left] + integral[(top * stride) + left];
                    mask[(y * width) + x] = picture.Pixels[(y * width) + x] * (double)count < sum * ratio;
                }
            }

            return mask;
        }
        finally
        {
            Array.Clear(integral);
        }
    }

    internal static List<Run> FindTextRuns(GrayBitmap picture, bool[] ink)
    {
        var width = picture.Width;
        var height = picture.Height;
        var profile = new float[height];
        for (var y = 0; y < height; y++)
        {
            var count = 0;
            for (var x = 0; x < width; x++)
            {
                if (ink[(y * width) + x])
                    count++;
            }

            profile[y] = count;
        }

        var smooth = new float[height];
        for (var y = 0; y < height; y++)
        {
            var from = Math.Max(0, y - 1);
            var to = Math.Min(height - 1, y + 1);
            float sum = 0;
            for (var k = from; k <= to; k++)
                sum += profile[k];

            smooth[y] = sum / (to - from + 1);
        }

        var start = (int)(height * SearchStart);
        var region = smooth[start..].OrderByDescending(v => v).Take(Math.Max(3, (height - start) / 50)).ToArray();
        var peak = region.Length == 0 ? 0 : region.Average();
        if (peak < width * 0.04)
            return [];

        var threshold = peak * 0.35;
        var runs = new List<(int Top, int Bottom)>();
        var openTop = -1;
        for (var y = start; y <= height; y++)
        {
            var dense = y < height && smooth[y] >= threshold;
            if (dense && openTop < 0)
            {
                openTop = y;
            }
            else if (!dense && openTop >= 0)
            {
                runs.Add((openTop, y - 1));
                openTop = -1;
            }
        }

        var merged = new List<(int Top, int Bottom)>();
        foreach (var run in runs)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                var gap = run.Top - previous.Bottom - 1;
                var larger = Math.Max(previous.Bottom - previous.Top + 1, run.Bottom - run.Top + 1);
                if (gap <= Math.Max(2, larger / 3))
                {
                    merged[^1] = (previous.Top, run.Bottom);
                    continue;
                }
            }

            merged.Add(run);
        }

        var minHeight = Math.Max(8, (int)(height * 0.008));
        var maxHeight = Math.Max(minHeight + 1, (int)(height * 0.09));
        return merged
            .Where(run => run.Bottom - run.Top + 1 >= minHeight && run.Bottom - run.Top + 1 <= maxHeight)
            .Select(run => new Run(run.Top, run.Bottom, 0, width - 1))
            .ToList();
    }

    private static MrzLine? CutLine(GrayBitmap picture, bool[] ink, Run run)
    {
        var width = picture.Width;
        var height = run.Height;
        var columns = new bool[width];
        for (var x = 0; x < width; x++)
        {
            for (var y = run.Top; y <= run.Bottom; y++)
            {
                if (ink[(y * width) + x])
                {
                    columns[x] = true;
                    break;
                }
            }
        }

        var maxGap = Math.Max(6, height);
        int bestLeft = -1, bestRight = -1, runLeft = -1, runRight = -1;
        for (var x = 0; x < width; x++)
        {
            if (!columns[x])
                continue;

            if (runLeft < 0 || x - runRight > maxGap)
                runLeft = x;

            runRight = x;
            if (bestLeft < 0 || runRight - runLeft > bestRight - bestLeft)
            {
                bestLeft = runLeft;
                bestRight = runRight;
            }
        }

        if (bestLeft < 0 || bestRight - bestLeft + 1 < height * MinAspect)
            return null;

        var marginX = Math.Max(4, (int)(height * 0.6));
        var marginY = Math.Max(3, (int)(height * 0.5));
        using var raw = picture.Cropped(bestLeft - marginX, run.Top - marginY, bestRight - bestLeft + 1 + (2 * marginX), height + (2 * marginY));
        if (raw is null)
            return null;

        var glare = SaturatedShare(raw);
        var blur = BlurRatio(raw);
        using var stretched = Stretch(raw);
        var factor = Math.Clamp(TargetLineHeight / height, 0.4, 3.0);
        var gray = Math.Abs(factor - 1.0) < 0.1
            ? new GrayBitmap(stretched.Width, stretched.Height, (byte[])stretched.Pixels.Clone())
            : stretched.Resized(Math.Max(16, (int)(stretched.Width * factor)), Math.Max(16, (int)(stretched.Height * factor)));
        var binaryMask = InkMask(gray, Math.Max(6, gray.Height / 2), 0.85);
        var binary = new byte[binaryMask.Length];
        for (var i = 0; i < binary.Length; i++)
            binary[i] = binaryMask[i] ? (byte)0 : (byte)255;

        Array.Clear(binaryMask);
        return new MrzLine(gray, new GrayBitmap(gray.Width, gray.Height, binary), run.Top, run.Bottom, bestLeft, bestRight, glare, blur);
    }

    internal static List<IReadOnlyList<int>> GroupLines(IReadOnlyList<Run> lines, int pictureWidth, int pictureHeight)
    {
        var groups = new List<(double Score, IReadOnlyList<int> Indices)>();
        for (var first = 0; first < lines.Count; first++)
        {
            foreach (var count in new[] { 3, 2 })
            {
                if (first + count > lines.Count)
                    continue;

                var window = Enumerable.Range(first, count).ToArray();
                if (!IsRegularBlock(lines, window))
                    continue;

                var bottom = lines[window[^1]].Bottom;
                var width = window.Max(i => lines[i].Width);
                var score = count + (2.0 * bottom / pictureHeight) + Math.Min(1.0, (double)width / pictureWidth);
                groups.Add((score, window));
            }
        }

        return groups.OrderByDescending(g => g.Score).Take(3).Select(g => g.Indices).ToList();
    }

    private static bool IsRegularBlock(IReadOnlyList<Run> lines, int[] window)
    {
        var first = lines[window[0]];
        var widest = window.Max(i => lines[i].Width);
        for (var k = 0; k < window.Length; k++)
        {
            var line = lines[window[k]];
            var ratio = (double)line.Height / first.Height;
            if (ratio < 0.6 || ratio > 1.6)
                return false;

            if (Math.Abs(line.Left - first.Left) > 0.08 * widest || Math.Abs(line.Right - first.Right) > 0.12 * widest)
                return false;

            if (k > 0)
            {
                var gap = line.Top - lines[window[k - 1]].Bottom - 1;
                if (gap < first.Height * 0.25 || gap > first.Height * 2.0)
                    return false;
            }
        }

        return true;
    }

    internal static double SaturatedShare(GrayBitmap picture)
    {
        var saturated = 0;
        foreach (var pixel in picture.Pixels)
        {
            if (pixel >= 250)
                saturated++;
        }

        return picture.Pixels.Length == 0 ? 0 : (double)saturated / picture.Pixels.Length;
    }

    internal static double BlurRatio(GrayBitmap picture)
    {
        var width = picture.Width;
        var height = picture.Height;
        if (width < 5 || height < 5)
            return 1;

        var blurred = new byte[picture.Pixels.Length];
        try
        {
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                            sum += picture.Pixels[((y + dy) * width) + x + dx];
                    }

                    blurred[(y * width) + x] = (byte)(sum / 9);
                }
            }

            double before = 0;
            double after = 0;
            for (var y = 1; y < height - 2; y++)
            {
                for (var x = 1; x < width - 2; x++)
                {
                    var at = (y * width) + x;
                    double dxBefore = picture.Pixels[at + 1] - picture.Pixels[at];
                    double dyBefore = picture.Pixels[at + width] - picture.Pixels[at];
                    double dxAfter = blurred[at + 1] - blurred[at];
                    double dyAfter = blurred[at + width] - blurred[at];
                    before += (dxBefore * dxBefore) + (dyBefore * dyBefore);
                    after += (dxAfter * dxAfter) + (dyAfter * dyAfter);
                }
            }

            return before <= 0 ? 1 : Math.Min(1.0, after / before);
        }
        finally
        {
            Array.Clear(blurred);
        }
    }

    internal static GrayBitmap Stretch(GrayBitmap picture)
    {
        var histogram = new int[256];
        foreach (var pixel in picture.Pixels)
            histogram[pixel]++;

        var total = picture.Pixels.Length;
        int low = 0, high = 255, running = 0;
        for (var level = 0; level < 256; level++)
        {
            running += histogram[level];
            if (running >= total * 0.01)
            {
                low = level;
                break;
            }
        }

        running = 0;
        for (var level = 255; level >= 0; level--)
        {
            running += histogram[level];
            if (running >= total * 0.01)
            {
                high = level;
                break;
            }
        }

        var pixels = new byte[picture.Pixels.Length];
        if (high - low < 16)
        {
            Buffer.BlockCopy(picture.Pixels, 0, pixels, 0, pixels.Length);
        }
        else
        {
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)Math.Clamp((picture.Pixels[i] - low) * 255 / (high - low), 0, 255);
        }

        return new GrayBitmap(picture.Width, picture.Height, pixels);
    }
}
