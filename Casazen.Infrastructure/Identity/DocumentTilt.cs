using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The edges of a gray copy of a picture of a document, as <see cref="DocumentGeometryInspector"/> reads them (E71/F01/US01/T01, IDENT-TILT): which pixels are strong edges of a mostly
/// horizontal line and which of a mostly vertical one, and how many of them each row and each column has. Held in memory only; <see cref="Dispose"/> zeroes every buffer (ADR-013-R2), and
/// every owner reaches it in a <c>finally</c>.
/// </summary>
internal sealed class DocumentEdgeMaps : IDisposable
{
    public DocumentEdgeMaps(int width, int height)
    {
        Width = width;
        Height = height;
        Horizontal = DocumentTilt.Track(new byte[width * height]);
        Vertical = DocumentTilt.Track(new byte[width * height]);
        RowEdges = DocumentTilt.Track(new int[height]);
        ColumnEdges = DocumentTilt.Track(new int[width]);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>1 where a strong edge is mostly horizontal (its gradient is mostly vertical).</summary>
    public byte[] Horizontal { get; }

    /// <summary>1 where a strong edge is mostly vertical (its gradient is mostly horizontal).</summary>
    public byte[] Vertical { get; }

    /// <summary>The pixels of a horizontal edge in every row.</summary>
    public int[] RowEdges { get; }

    /// <summary>The pixels of a vertical edge in every column.</summary>
    public int[] ColumnEdges { get; }

    public void Dispose()
    {
        Array.Clear(Horizontal);
        Array.Clear(Vertical);
        Array.Clear(RowEdges);
        Array.Clear(ColumnEdges);
    }
}

/// <summary>
/// The strong edge pixels of a picture as points (their distance from the centre of the picture): the ones of horizontal edges and the ones of vertical edges. They are what the angle of a card is
/// read from: how sharply they pile up in rows and columns when the picture is turned (IDENT-TILT). Held in memory only; <see cref="Dispose"/> zeroes them.
/// </summary>
internal sealed class DocumentEdgePoints : IDisposable
{
    private readonly float[] _horizontalX;
    private readonly float[] _horizontalY;
    private readonly float[] _verticalX;
    private readonly float[] _verticalY;
    private readonly int[] _rows;
    private readonly int[] _columns;
    private readonly int _offset;

    private DocumentEdgePoints(DocumentEdgeMaps edges)
    {
        var width = edges.Width;
        var height = edges.Height;
        _horizontalX = DocumentTilt.Track(new float[edges.RowEdges.Sum()]);
        _horizontalY = DocumentTilt.Track(new float[_horizontalX.Length]);
        _verticalX = DocumentTilt.Track(new float[edges.ColumnEdges.Sum()]);
        _verticalY = DocumentTilt.Track(new float[_verticalX.Length]);
        _offset = (int)Math.Ceiling(Math.Sqrt(((double)width * width) + ((double)height * height)) / 2) + 2;
        _rows = DocumentTilt.Track(new int[(2 * _offset) + 1]);
        _columns = DocumentTilt.Track(new int[(2 * _offset) + 1]);

        var centreX = (width - 1) / 2.0;
        var centreY = (height - 1) / 2.0;
        var horizontal = 0;
        var vertical = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var at = (y * width) + x;
                if (edges.Horizontal[at] != 0)
                {
                    _horizontalX[horizontal] = (float)(x - centreX);
                    _horizontalY[horizontal++] = (float)(y - centreY);
                }
                else if (edges.Vertical[at] != 0)
                {
                    _verticalX[vertical] = (float)(x - centreX);
                    _verticalY[vertical++] = (float)(y - centreY);
                }
            }
        }
    }

    /// <summary>The edge pixels of the picture as points.</summary>
    public static DocumentEdgePoints From(DocumentEdgeMaps edges) => new(edges);

    /// <summary>The number of edge pixels there are.</summary>
    public int Count => _horizontalX.Length + _verticalX.Length;

    /// <summary>
    /// How sharply the edges pile up in rows and in columns when the picture is turned <paramref name="degrees"/> counter-clockwise (the sum of the squares of the edge pixels of every row of the
    /// horizontal edges and of every column of the vertical ones): a long straight edge that the turn makes level falls in one row, so a card whose sides run along the turned rows and columns
    /// scores far more than at any other angle, and the long lines of the card (its sides, the lines of its text) weigh more than anything short. A card turned <paramref name="degrees"/>
    /// clockwise scores most at that angle.
    /// </summary>
    public long Sharpness(double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = (float)Math.Cos(radians);
        var sin = (float)Math.Sin(radians);
        Array.Clear(_rows);
        Array.Clear(_columns);
        var shift = _offset + 0.5f;
        for (var i = 0; i < _horizontalX.Length; i++)
            _rows[(int)((cos * _horizontalY[i]) - (sin * _horizontalX[i]) + shift)]++;

        for (var i = 0; i < _verticalX.Length; i++)
            _columns[(int)((cos * _verticalX[i]) + (sin * _verticalY[i]) + shift)]++;

        long sharpness = 0;
        for (var i = 0; i < _rows.Length; i++)
            sharpness += ((long)_rows[i] * _rows[i]) + ((long)_columns[i] * _columns[i]);

        return sharpness;
    }

    public void Dispose()
    {
        Array.Clear(_horizontalX);
        Array.Clear(_horizontalY);
        Array.Clear(_verticalX);
        Array.Clear(_verticalY);
        Array.Clear(_rows);
        Array.Clear(_columns);
    }
}

/// <summary>
/// The search of the angle of a card that is not held straight, and the copies of a picture it levels (IDENT-TILT, NF-189). Pure and managed: no new package, no native code, no file, no
/// network, no logger, no state. <b>Method.</b> The strong edge pixels of the picture are turned by every angle within <see cref="DocumentGeometryRules.TiltSearchDegrees"/> of none, a coarse step
/// (<see cref="DocumentGeometryRules.TiltCoarseStepDegrees"/>) at a time, and at each how sharply they pile up in rows and in columns is the score of the angle
/// (<see cref="DocumentEdgePoints.Sharpness"/>, the edge support the inspector reads the sides of a card by, summed over the picture): the long straight lines of a card, its sides and the lines of
/// its text, all fall in a row or a column at one angle. The best <see cref="DocumentGeometryRules.TiltCandidates"/> peaks that stand out of the scores
/// (<see cref="DocumentGeometryRules.TiltPeakFactor"/> times their median) are the angles tried, each refined by a fine scan around it (<see cref="DocumentGeometryRules.TiltFineStepDegrees"/>,
/// the middle of the angles that score nearly as well). The inspector levels the picture by an angle (<see cref="Level"/>) and runs on it the inspection that reads a card as it lies. Nothing here
/// decides anything about a person: it says where a card lies. Every copy it makes is the caller's, who zeroes it.
/// </summary>
internal static class DocumentTilt
{
    /// <summary>
    /// A test seam: when a test sets it on its thread, every buffer made of a picture here (the edge maps and the points, the levelled copies, the levelled card) is added to it, so the test can
    /// prove that every one of them is zeroed when the inspection returns. Null in the application: nothing is recorded.
    /// </summary>
    [ThreadStatic]
    internal static List<Array>? Probe;

    /// <summary>Records <paramref name="buffer"/> for the test seam and returns it.</summary>
    internal static T[] Track<T>(T[] buffer)
    {
        Probe?.Add(buffer);
        return buffer;
    }

    /// <summary>
    /// The angles, in degrees clockwise, the card of a picture may be turned by, the likeliest first, at most <paramref name="maxCount"/> of them: the peaks of the score of the angles of
    /// <paramref name="points"/> within <paramref name="searchDegrees"/> either way, each of at least <see cref="DocumentGeometryRules.TiltPeakFactor"/> times the median score, two of them at
    /// least <see cref="DocumentGeometryRules.TiltPeakSeparationDegrees"/> apart, each refined to <see cref="DocumentGeometryRules.TiltFineStepDegrees"/>.
    /// </summary>
    internal static IReadOnlyList<double> Angles(DocumentEdgePoints points, double searchDegrees, int maxCount)
    {
        var coarse = DocumentGeometryRules.TiltCoarseStepDegrees;
        var half = (int)Math.Round(searchDegrees / coarse);
        var scores = new long[(2 * half) + 1];
        for (var i = 0; i < scores.Length; i++)
            scores[i] = points.Sharpness((i - half) * coarse);

        var median = scores.Order().ElementAt(scores.Length / 2);
        var peaks = Enumerable.Range(0, scores.Length)
            .Where(i => (i == 0 || scores[i] >= scores[i - 1]) && (i == scores.Length - 1 || scores[i] >= scores[i + 1]))
            .Where(i => scores[i] > 0 && scores[i] >= DocumentGeometryRules.TiltPeakFactor * median)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => Math.Abs(i - half))
            .ToList();
        var picked = new List<int>();
        foreach (var peak in peaks)
        {
            if (picked.Any(other => Math.Abs(other - peak) * coarse < DocumentGeometryRules.TiltPeakSeparationDegrees))
                continue;

            picked.Add(peak);
            if (picked.Count == maxCount)
                break;
        }

        return picked.Select(peak => Refined(points, (peak - half) * coarse)).ToList();
    }

    /// <summary>
    /// <paramref name="degrees"/> refined: the angles within a coarse step either way are scanned by <see cref="DocumentGeometryRules.TiltFineStepDegrees"/>, and the angle is the middle (the
    /// score-weighted mean) of the ones that score at least <see cref="DocumentGeometryRules.TiltPlateauShare"/> of the best.
    /// </summary>
    internal static double Refined(DocumentEdgePoints points, double degrees)
    {
        var step = DocumentGeometryRules.TiltFineStepDegrees;
        var count = (int)Math.Round(DocumentGeometryRules.TiltCoarseStepDegrees / step);
        var scores = new long[(2 * count) + 1];
        for (var i = 0; i < scores.Length; i++)
            scores[i] = points.Sharpness(degrees + ((i - count) * step));

        var best = scores.Max();
        double weight = 0;
        double sum = 0;
        for (var i = 0; i < scores.Length; i++)
        {
            if (scores[i] < DocumentGeometryRules.TiltPlateauShare * best)
                continue;

            weight += scores[i];
            sum += scores[i] * (degrees + ((i - count) * step));
        }

        return Math.Round(sum / weight, 2);
    }

    /// <summary>
    /// A copy of <paramref name="source"/> with its content turned <paramref name="degrees"/> counter-clockwise around the centre (a card turned <paramref name="degrees"/> clockwise lies
    /// straight in it), bilinear, of the same size. What the turn uncovers at the border takes the value of the nearest pixel of the picture, so no edge is made where the picture ends. The
    /// caller zeroes the copy.
    /// </summary>
    internal static GrayBitmap Level(GrayBitmap source, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var width = source.Width;
        var height = source.Height;
        var centreX = (width - 1) / 2.0;
        var centreY = (height - 1) / 2.0;
        var maxX = width - 1;
        var maxY = height - 1;
        var from = source.Pixels;
        var pixels = Track(new byte[from.Length]);
        for (var y = 0; y < height; y++)
        {
            var dy = y - centreY;
            var rowX = centreX - (sin * dy);
            var rowY = centreY + (cos * dy);
            for (var x = 0; x < width; x++)
            {
                var dx = x - centreX;
                var sourceX = Math.Clamp(rowX + (cos * dx), 0, maxX);
                var sourceY = Math.Clamp(rowY + (sin * dx), 0, maxY);
                var left = (int)sourceX;
                var top = (int)sourceY;
                var right = Math.Min(left + 1, maxX);
                var bottom = Math.Min(top + 1, maxY);
                var shareX = sourceX - left;
                var shareY = sourceY - top;
                var upper = (from[(top * width) + left] * (1 - shareX)) + (from[(top * width) + right] * shareX);
                var lower = (from[(bottom * width) + left] * (1 - shareX)) + (from[(bottom * width) + right] * shareX);
                pixels[(y * width) + x] = (byte)((upper * (1 - shareY)) + (lower * shareY) + 0.5);
            }
        }

        return new GrayBitmap(width, height, pixels);
    }

    /// <summary>
    /// Where a point of the levelled gray copy (<see cref="Level"/> by <paramref name="degrees"/>) is in the gray copy as it lies, in pixels of the copy, both of <paramref name="width"/> by
    /// <paramref name="height"/>.
    /// </summary>
    internal static (double X, double Y) ToPicture(double degrees, int width, int height, double levelledX, double levelledY)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var centreX = (width - 1) / 2.0;
        var centreY = (height - 1) / 2.0;
        var dx = levelledX - centreX;
        var dy = levelledY - centreY;
        return (centreX + (cos * dx) - (sin * dy), centreY + (sin * dx) + (cos * dy));
    }

    /// <summary>
    /// The card of a levelled gray copy cut out of the colour picture, upright as it lies and not yet turned by its quarter turns: the rectangle <paramref name="left"/>, <paramref name="top"/>,
    /// <paramref name="right"/>, <paramref name="bottom"/> (lines of the levelled copy, <paramref name="grayWidth"/> by <paramref name="grayHeight"/> pixels, levelled by
    /// <paramref name="degrees"/>) sampled in <paramref name="image"/> at its own resolution, bilinear, in one pass (no levelled copy of the whole picture is made). The caller zeroes the copy.
    /// </summary>
    internal static RasterImage LevelCard(RasterImage image, int grayWidth, int grayHeight, double degrees, int left, int top, int right, int bottom)
    {
        var scaleX = (double)image.Width / grayWidth;
        var scaleY = (double)image.Height / grayHeight;
        var width = Math.Max(1, (int)Math.Round((right - left) * scaleX));
        var height = Math.Max(1, (int)Math.Round((bottom - top) * scaleY));
        var stepX = (right - left) / (double)width;
        var stepY = (bottom - top) / (double)height;
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var centreX = (grayWidth - 1) / 2.0;
        var centreY = (grayHeight - 1) / 2.0;
        var maxX = image.Width - 1;
        var maxY = image.Height - 1;
        var from = image.Rgb;
        var rgb = Track(new byte[checked(width * height * 3)]);
        for (var v = 0; v < height; v++)
        {
            var dy = top + (v * stepY) - centreY;
            var dxStart = left - centreX;
            var rowX = centreX + (cos * dxStart) - (sin * dy);
            var rowY = centreY + (sin * dxStart) + (cos * dy);
            for (var u = 0; u < width; u++)
            {
                var pictureX = rowX + (u * stepX * cos);
                var pictureY = rowY + (u * stepX * sin);
                var sourceX = Math.Clamp(((pictureX + 0.5) * scaleX) - 0.5, 0, maxX);
                var sourceY = Math.Clamp(((pictureY + 0.5) * scaleY) - 0.5, 0, maxY);
                var leftColumn = (int)sourceX;
                var topRow = (int)sourceY;
                var rightColumn = Math.Min(leftColumn + 1, maxX);
                var bottomRow = Math.Min(topRow + 1, maxY);
                var shareX = sourceX - leftColumn;
                var shareY = sourceY - topRow;
                var topLeft = ((topRow * image.Width) + leftColumn) * 3;
                var topRight = ((topRow * image.Width) + rightColumn) * 3;
                var bottomLeft = ((bottomRow * image.Width) + leftColumn) * 3;
                var bottomRight = ((bottomRow * image.Width) + rightColumn) * 3;
                var at = ((v * width) + u) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var upper = (from[topLeft + channel] * (1 - shareX)) + (from[topRight + channel] * shareX);
                    var lower = (from[bottomLeft + channel] * (1 - shareX)) + (from[bottomRight + channel] * shareX);
                    rgb[at + channel] = (byte)((upper * (1 - shareY)) + (lower * shareY) + 0.5);
                }
            }
        }

        return new RasterImage(width, height, rgb);
    }
}
