using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// Says whether a photo shows the WHOLE card of the expected format, upright, with its edges visible and its portrait where the layout puts it (E71/F01/US01/T01,
/// NF-189, decisions D1 and D5). Pure and managed: no new package, no native code, no file, no network, no logger, no state; the picture is the caller's and is not
/// touched, every copy made here is zeroed before returning (ADR-013-R2). The inspector decides nothing but the codes of <see cref="IdentityGuidanceCodes"/>: the flow
/// turns them into guidance (E71/F01/US03/T01), the policy and the format file hold every number of a decision.
/// <para>
/// <b>Algorithm.</b> (1) A gray copy of at most <see cref="CaptureQualityRules.StatisticsSide"/> pixels (the one <c>CaptureQualityAssessor</c> uses; the picture already
/// has its EXIF orientation and its pixel limit, <c>RasterImage.Decode</c>). (2) The Sobel gradient; the strong pixels whose gradient is mostly vertical are the pixels
/// of horizontal edges, the ones whose gradient is mostly horizontal are the pixels of vertical edges. (3) The projection profiles of those two maps (the edge pixels per
/// row and per column) give the rows and the columns where a long straight edge may be; the border of the picture is added to each list, for a card that runs out of the
/// frame. (4) The card is the largest rectangle bounded by such rows and columns whose sides are straight edges: a side is a line of the picture when one row (or column)
/// within a pixel or two of it has an edge pixel along three quarters of the side, so a structure inside the card (the portrait is 62% of its height) is not a side. Of the
/// rectangles that cover at least <c>Layout.MinCardAreaShare</c>, the one with the most sides that are lines of the picture (a side that is the border of the picture is a card
/// cut by the frame), then the one of the ratio of the format, then the largest. There is no search of the angle in this slice (the guided capture keeps the card straight, and
/// the zones of the format are rectangles of the card), so the tolerance is small. <b>Measured</b> (<c>DocumentGeometryInspectorTests</c>: the card of
/// <c>DocumentImageFactory</c>, 480 by 303 pixels in a picture of 640 by 480, its edges softened by a bilinear turn as in a photo): a card turned by up to 0.2 degrees either way
/// is found whole; from 0.35 degrees (0.25 on most of the pictures) it is not found at all, <c>document_not_found</c>, up to the 10 degrees tried (the front turned 0.25 and
/// 0.3 degrees counter-clockwise was found as a small rectangle and asked for as <c>document_edges_not_visible</c>); never a whole card of its layout. Real photos may not
/// behave the same, and a card that is not held straight to a fraction of a degree is asked for again: the search of the angle is a slice of its own.
/// (5) <see cref="DocumentGeometryReport.EdgesVisible"/>, <see cref="DocumentGeometryReport.AspectOk"/> and the rotation of 0 or 90 degrees follow from the rectangle and
/// <c>Layout</c>. (6) On a measurable card (whole, visible, of the ratio) the portrait is looked for with <c>DocumentPortrait</c>, which tries the picture upright and
/// turned by 90, 270 and 180 degrees and keeps the turn at which the detector is surest. The turn of a side that carries the MRZ band and no portrait (the back) comes from the
/// band (<c>Layout.Sides[side].MrzRegion</c>: the band is the densest text of a document, so the card is upright when the band has more edges than the opposite region) BEFORE
/// any face: a face found on such a side is a front sent as the back or a stray detection, and only says where a portrait is (for the classifier of the side), never how the
/// card lies. On any other side the turn is the one at which the face is found, and a card with no face to tell it takes its turn from the band of the side.
/// (7) Glare and blur are measured with the statistics of the capture checks, on the card.
/// </para>
/// <para>
/// <b>Numbers.</b> The numbers of the layout come from the format file (<c>aspectRatio</c>, <c>aspectTolerance</c>, <c>minCardAreaShare</c>, <c>marginMin</c> and the portrait
/// zone), the score of the face detector from <c>Biometrics:DocumentDetectionScoreThreshold</c>, the limits of glare and blur from <see cref="CaptureQualityRules"/>.
/// The parameters of the edge detector (how strong an edge is, how much of a side must be an edge for it to be a line of the picture, how small a card is still looked for)
/// are <see cref="DocumentGeometryRules"/>. Two of them, <see cref="DocumentGeometryRules.SideSupportMin"/> and <see cref="DocumentGeometryRules.SmallestCardSideDivisor"/>, do
/// decide whether a card is whole or found at all, so the whole set is versioned (<see cref="DocumentGeometryRules.Version"/>, carried by every report as
/// <see cref="DocumentGeometryReport.RulesVersion"/>) and a test holds each value next to its version: nobody can change one without changing the version.
/// </para>
/// <para>
/// <b>Fail closed.</b> The inspector never approves anything. When the face models are not available <see cref="BiometricUnavailableException"/> reaches the caller (it
/// is the failure of a model: the flow sends the verification to a person) and so does any other fault of the detector; a cancellation is an
/// <see cref="OperationCanceledException"/>. Nothing is logged here: an exception is described by its type by whoever catches it.
/// </para>
/// </summary>
public sealed class DocumentGeometryInspector(IFaceDetector faceDetector, IOptions<BiometricModelOptions> options)
{
    /// <summary>The largest value of <c>|gx| + |gy|</c> of the Sobel gradient of a picture of 8 bits (4 * 255 on each axis): a bound of the arithmetic, not a rule.</summary>
    private const int MaxGradient = 2040;

    /// <summary>
    /// Inspects <paramref name="image"/>, a picture of the identity document of <paramref name="format"/> sent as <paramref name="declaredSide"/>. The numbers of the format
    /// are read from <see cref="DocumentFormat.Layout"/>; <paramref name="declaredSide"/> says which side's portrait (<c>document_portrait_missing</c>) and MRZ band (the
    /// rotation of a back) are asked for. Throws <see cref="BiometricUnavailableException"/> when the face models are not available (see the class).
    /// </summary>
    public DocumentGeometryReport Inspect(RasterImage image, DocumentFormat format, DocumentSide declaredSide, CancellationToken cancellationToken) =>
        Inspect(image, format, declaredSide, lookForPortrait: true, cancellationToken);

    /// <summary>
    /// <see cref="Inspect(RasterImage, DocumentFormat, DocumentSide, CancellationToken)"/> with a choice about the face (E71/F01/US03/T01): with <paramref name="lookForPortrait"/> false
    /// the face detector is never asked, so the report says nothing about the portrait (<see cref="DocumentGeometryReport.PortraitInZone"/> is null, no
    /// <c>document_portrait_missing</c>) and the turn of the card comes from its box and the MRZ band of the declared side only (a landscape card is taken as upright, a tall one that
    /// no band can turn is not decided). The prefill of <c>read-document</c> uses it for a guest who has not given the current consent: the face of the document is looked for only after it.
    /// </summary>
    public DocumentGeometryReport Inspect(
        RasterImage image, DocumentFormat format, DocumentSide declaredSide, bool lookForPortrait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(format);
        cancellationToken.ThrowIfCancellationRequested();

        var layout = format.Layout;
        using var gray = CaptureQualityAssessor.Statistics(image, null)!;
        var card = FindCard(gray, layout, cancellationToken);
        if (card is null)
        {
            var (wholeGlare, wholeBlur) = Quality(gray);
            return new DocumentGeometryReport(
                false, null, false, 0, false, 0, null, wholeGlare, wholeBlur, QualityCodes([IdentityGuidanceCodes.DocumentNotFound], wholeGlare, wholeBlur),
                DocumentGeometryRules.Version);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var box = ToBox(card, gray.Width, gray.Height);
        var ratio = LongOverShort(box.W * image.Width, box.H * image.Height);
        var allSidesSeen = card.SeenSides == 4;
        var aspectOk = allSidesSeen && Math.Abs(ratio - layout.AspectRatio) <= layout.AspectTolerance;
        var margins = box.X >= layout.MarginMin && box.Y >= layout.MarginMin
            && 1 - (box.X + box.W) >= layout.MarginMin && 1 - (box.Y + box.H) >= layout.MarginMin;
        var edgesVisible = allSidesSeen && margins && box.W * box.H >= layout.MinCardAreaShare;

        using var cardGray = gray.Cropped(card.LeftEdge, card.TopEdge, card.RightEdge(gray.Width) - card.LeftEdge + 1, card.BottomEdge(gray.Height) - card.TopEdge + 1);
        var (glare, blur) = Quality(cardGray ?? gray);

        var orientation = new Orientation(0, true, null, false);
        if (edgesVisible && aspectOk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tall = box.H * image.Height > box.W * image.Width;
            orientation = ResolveOrientation(image, cardGray, box, tall, format, declaredSide, lookForPortrait, cancellationToken);
        }

        var codes = new List<string>();
        if (!edgesVisible)
        {
            codes.Add(IdentityGuidanceCodes.DocumentEdgesNotVisible);
        }
        else if (!aspectOk)
        {
            codes.Add(IdentityGuidanceCodes.DocumentLayoutMismatch);
        }
        else
        {
            var declaredHasPortrait = PortraitZone(format) is { } zone && string.Equals(zone.Side, DocumentSides.CodeOf(declaredSide), StringComparison.Ordinal);
            if (declaredHasPortrait && orientation.PortraitFarOutside)
                codes.Add(IdentityGuidanceCodes.DocumentLayoutMismatch);
            if (!orientation.Decided)
                codes.Add(IdentityGuidanceCodes.DocumentOrientation);
            if (declaredHasPortrait && orientation.PortraitInZone == false && !orientation.PortraitFarOutside)
                codes.Add(IdentityGuidanceCodes.DocumentPortraitMissing);
        }

        return new DocumentGeometryReport(
            true, box, edgesVisible, ratio, aspectOk, orientation.Rotation, orientation.PortraitInZone, glare, blur, QualityCodes(codes, glare, blur),
            DocumentGeometryRules.Version);
    }

    /// <summary>The card as the rectangle found makes it: its sides in pixels of the gray copy, and which of them are lines of the picture (the others are the border of the picture).</summary>
    private sealed record CardRectangle(int Left, int Top, int Right, int Bottom, bool LeftSeen, bool TopSeen, bool RightSeen, bool BottomSeen)
    {
        /// <summary>How many sides are lines of the picture (4: the card is whole).</summary>
        public int SeenSides => (LeftSeen ? 1 : 0) + (TopSeen ? 1 : 0) + (RightSeen ? 1 : 0) + (BottomSeen ? 1 : 0);

        /// <summary>The first column of the card (the border of the picture when the left side runs out of it).</summary>
        public int LeftEdge => LeftSeen ? Left : 0;

        /// <summary>The first row of the card.</summary>
        public int TopEdge => TopSeen ? Top : 0;

        /// <summary>The last column of the card in a picture of <paramref name="width"/> pixels.</summary>
        public int RightEdge(int width) => RightSeen ? Right : width - 1;

        /// <summary>The last row of the card in a picture of <paramref name="height"/> pixels.</summary>
        public int BottomEdge(int height) => BottomSeen ? Bottom : height - 1;
    }

    /// <summary>What decides which rectangle is the card: the plausible ones first, then the one with the most sides that are lines of the picture, then the one of the ratio, then the largest.</summary>
    private readonly record struct Rank(bool Plausible, int SeenSides, bool AspectMatch, long Area);

    /// <summary>The turn of a card, whether it could be decided, and where the portrait is (see <see cref="ResolveOrientation"/>).</summary>
    private sealed record Orientation(int Rotation, bool Decided, bool? PortraitInZone, bool PortraitFarOutside);

    /// <summary>A face found in the card upright: the turn at which it was found and its box, as shares of the upright card.</summary>
    private sealed record PortraitEvidence(int ExifOrientation, double CenterX, double CenterY, double Left, double Top, double Right, double Bottom);

    private static NormalizedBox ToBox(CardRectangle card, int width, int height)
    {
        var left = card.LeftEdge;
        var top = card.TopEdge;
        var right = card.RightSeen ? card.Right : width;
        var bottom = card.BottomSeen ? card.Bottom : height;
        return new NormalizedBox((double)left / width, (double)top / height, (double)(right - left) / width, (double)(bottom - top) / height);
    }

    private static double LongOverShort(double width, double height)
    {
        var shortest = Math.Min(width, height);
        return shortest <= 0 ? 0 : Math.Max(width, height) / shortest;
    }

    private static (double Glare, double Blur) Quality(GrayBitmap region) =>
        (MrzImageLocator.SaturatedShare(region), MrzImageLocator.BlurRatio(region));

    /// <summary>The codes of the card, then the ones of the glare and the blur against the limits of the capture checks.</summary>
    private static IReadOnlyList<string> QualityCodes(IEnumerable<string> codes, double glare, double blur)
    {
        var all = codes.ToList();
        if (glare > CaptureQualityRules.MaxDocumentGlareShare)
            all.Add(IdentityGuidanceCodes.DocumentGlare);
        if (blur > CaptureQualityRules.MaxBlurRatio)
            all.Add(IdentityGuidanceCodes.DocumentBlur);
        return all;
    }

    /// <summary>
    /// The turn of a measurable card, and where its portrait is. A side that carries the MRZ band and no portrait (the back) is turned by its band BEFORE any face: a face found
    /// on it (a front sent as the back, or a stray detection) only says where a portrait is, for the classifier of the side, and never how the card lies, so a face found
    /// sideways does not leave the back undecided and one found at a half turn does not turn it. On any other side a format with a portrait zone looks for the face in the card
    /// made landscape (a tall card is turned a quarter clockwise first): found upright, the turn is the one of the card; found at a half turn, 180 degrees more; found only
    /// sideways, the card cannot be turned (it is not decided and the portrait is not judged). With no face, or no zone, the MRZ band of the declared side tells the turn; with
    /// neither, a landscape card is taken as upright and a tall one is not decided.
    /// </summary>
    private Orientation ResolveOrientation(
        RasterImage image,
        GrayBitmap? cardGray,
        NormalizedBox box,
        bool tall,
        DocumentFormat format,
        DocumentSide declaredSide,
        bool lookForPortrait,
        CancellationToken cancellationToken)
    {
        var baseRotation = tall ? 90 : 0;
        var zone = PortraitZone(format);

        // Without the permission to look for a face the detector is not asked: no evidence, and the portrait is not judged (null, never «missing»).
        var evidence = zone is null || !lookForPortrait ? null : FindPortrait(image, box, tall, cancellationToken);
        format.Layout.Sides.TryGetValue(DocumentSides.CodeOf(declaredSide), out var declaredLayout);

        if (declaredLayout is { MrzRegion: { } band, Portrait: null })
        {
            var (bandRotation, bandDecided) = MrzBandTurn(cardGray, tall, band);
            if (bandDecided)
            {
                var (bandInZone, bandFarOutside) = lookForPortrait ? JudgePortrait(zone, evidence) : (null, false);
                return new Orientation(bandRotation, true, bandInZone, bandFarOutside);
            }
        }

        if (zone is not null && evidence is not null)
        {
            if (evidence.ExifOrientation is 6 or 8)
                return new Orientation(baseRotation, false, null, false);

            var rotation = evidence.ExifOrientation == 3 ? (baseRotation + 180) % 360 : baseRotation;
            var (inZone, farOutside) = JudgePortrait(zone, evidence);
            return new Orientation(rotation, true, inZone, farOutside);
        }

        var (cueRotation, cueDecided) = MrzBandTurn(cardGray, tall, declaredLayout?.MrzRegion);
        return new Orientation(cueRotation, cueDecided, zone is null || !lookForPortrait ? null : false, false);
    }

    /// <summary>
    /// Where the portrait is, from the face found on the card (null when none): <c>(null, false)</c> when no side of the format has a portrait zone or the face was found only
    /// sideways (not judged), <c>(false, false)</c> with no face, else whether the centre of the face is in the zone and whether the face is far outside it (no area in common).
    /// </summary>
    private static (bool? InZone, bool FarOutside) JudgePortrait((string Side, DocumentFormatRegion Region)? zone, PortraitEvidence? evidence)
    {
        if (zone is not { } portraitZone)
            return (null, false);

        if (evidence is null)
            return (false, false);

        if (evidence.ExifOrientation is 6 or 8)
            return (null, false);

        var inZone = Contains(portraitZone.Region, evidence.CenterX, evidence.CenterY);
        return (inZone, !inZone && !Overlaps(portraitZone.Region, evidence));
    }

    /// <summary>
    /// The face of the card, looked for with <see cref="DocumentPortrait.FindOriented"/> in the card cut out of the picture, brought within the input of the detector and made
    /// landscape; null when there is none. The copies are zeroed.
    /// </summary>
    private PortraitEvidence? FindPortrait(RasterImage image, NormalizedBox box, bool tall, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var crop = CropCard(image, box, settings.DetectorInputSize);
        using var turned = tall ? crop.Oriented(6) : null;
        var upright = turned ?? crop;
        var face = DocumentPortrait.FindOriented(faceDetector, upright, settings, cancellationToken, out var rotated, out var exifOrientation);
        using (rotated)
        {
            if (face is null)
                return null;

            var frame = rotated ?? upright;
            var left = face.X / frame.Width;
            var top = face.Y / frame.Height;
            var right = (face.X + face.Width) / frame.Width;
            var bottom = (face.Y + face.Height) / frame.Height;
            return new PortraitEvidence(exifOrientation, (left + right) / 2, (top + bottom) / 2, left, top, right, bottom);
        }
    }

    /// <summary>The card cut out of <paramref name="image"/> and shrunk, when it is larger, to <paramref name="longestSide"/> pixels (what the detector scales to anyway).</summary>
    private static RasterImage CropCard(RasterImage image, NormalizedBox box, int longestSide)
    {
        var left = (int)Math.Round(box.X * image.Width);
        var top = (int)Math.Round(box.Y * image.Height);
        var width = Math.Max(1, (int)Math.Round(box.W * image.Width));
        var height = Math.Max(1, (int)Math.Round(box.H * image.Height));
        var crop = image.Cropped(left, top, width, height);
        var longest = Math.Max(crop.Width, crop.Height);
        if (longest <= longestSide)
            return crop;

        try
        {
            var scale = (double)longestSide / longest;
            return crop.Resized(Math.Max(1, (int)Math.Round(crop.Width * scale)), Math.Max(1, (int)Math.Round(crop.Height * scale)));
        }
        finally
        {
            crop.Dispose();
        }
    }

    /// <summary>
    /// The turn the MRZ band tells: the band is the densest text of the card, so the card is upright when the band has more edges than the region opposite to it (the
    /// band turned by 180 degrees), and upside down when it has fewer. Equal (or no band to look at) is no evidence: a landscape card is taken as upright, a tall one is
    /// not decided.
    /// </summary>
    private static (int Rotation, bool Decided) MrzBandTurn(GrayBitmap? cardGray, bool tall, DocumentFormatRegion? mrz)
    {
        var baseRotation = tall ? 90 : 0;
        if (cardGray is null || mrz is null)
            return (baseRotation, !tall);

        using var turned = tall ? cardGray.Oriented(6) : null;
        var upright = turned ?? cardGray;
        var expected = RegionEdges(upright, mrz);
        var opposite = RegionEdges(upright, new DocumentFormatRegion(1 - mrz.X - mrz.W, 1 - mrz.Y - mrz.H, mrz.W, mrz.H));
        if (opposite > expected)
            return ((baseRotation + 180) % 360, true);

        return (baseRotation, expected > opposite || !tall);
    }

    /// <summary>The mean of the differences between neighbouring pixels (horizontal and vertical) in a region of the card, 0 to 510: how much text and structure the region has.</summary>
    private static double RegionEdges(GrayBitmap card, DocumentFormatRegion region)
    {
        var width = card.Width;
        var left = Math.Clamp((int)Math.Round(region.X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Round(region.Y * card.Height), 0, card.Height - 1);
        var right = Math.Clamp((int)Math.Round((region.X + region.W) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Round((region.Y + region.H) * card.Height), top + 1, card.Height);
        long sum = 0;
        long count = 0;
        for (var y = top; y < bottom - 1; y++)
        {
            for (var x = left; x < right - 1; x++)
            {
                var at = (y * width) + x;
                sum += Math.Abs(card.Pixels[at + 1] - card.Pixels[at]) + Math.Abs(card.Pixels[at + width] - card.Pixels[at]);
                count++;
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }

    /// <summary>The side of the format that has the portrait and its zone on the canonical card; null when no side of the format has one.</summary>
    private static (string Side, DocumentFormatRegion Region)? PortraitZone(DocumentFormat format)
    {
        foreach (var (side, sideLayout) in format.Layout.Sides)
        {
            if (sideLayout.Portrait is { } region)
                return (side, region);
        }

        return null;
    }

    private static bool Contains(DocumentFormatRegion zone, double x, double y) =>
        x >= zone.X && x <= zone.X + zone.W && y >= zone.Y && y <= zone.Y + zone.H;

    /// <summary>The box of the face and the zone share some area: a face that does not is far from the zone (the layout is not the one of the format).</summary>
    private static bool Overlaps(DocumentFormatRegion zone, PortraitEvidence face) =>
        face.Left < zone.X + zone.W && face.Right > zone.X && face.Top < zone.Y + zone.H && face.Bottom > zone.Y;

    /// <summary>
    /// The card of the gray copy, or null when the picture has none: the largest rectangle bounded by rows and columns where a long straight edge is (see the class), with the
    /// border of the picture for a side that runs out of the frame. Every buffer made from the picture is zeroed.
    /// </summary>
    private static CardRectangle? FindCard(GrayBitmap gray, DocumentFormatLayout layout, CancellationToken cancellationToken)
    {
        var width = gray.Width;
        var height = gray.Height;
        if (width < DocumentGeometryRules.MinWorkingSide || height < DocumentGeometryRules.MinWorkingSide)
            return null;

        short[]? gradientX = null;
        short[]? gradientY = null;
        byte[]? horizontal = null;
        byte[]? vertical = null;
        int[]? rowEdges = null;
        int[]? columnEdges = null;
        int[]? rowPrefix = null;
        int[]? columnPrefix = null;
        try
        {
            gradientX = new short[width * height];
            gradientY = new short[width * height];
            var threshold = Gradients(gray, gradientX, gradientY);
            cancellationToken.ThrowIfCancellationRequested();

            horizontal = new byte[width * height];
            vertical = new byte[width * height];
            rowEdges = new int[height];
            columnEdges = new int[width];
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var at = (y * width) + x;
                    var across = Math.Abs((int)gradientX[at]);
                    var down = Math.Abs((int)gradientY[at]);
                    if (across + down < threshold)
                        continue;

                    if (down >= DocumentGeometryRules.DominanceFactor * across)
                    {
                        horizontal[at] = 1;
                        rowEdges[y]++;
                    }
                    else if (across >= DocumentGeometryRules.DominanceFactor * down)
                    {
                        vertical[at] = 1;
                        columnEdges[x]++;
                    }
                }
            }

            // A card smaller than the format asks for is still a card (its edges are not visible: it is too small, not missing), so the shortest side looked for is a share of
            // the picture, not of the area of the format; a line shorter than the support asked of such a side cannot be one.
            var smallest = (double)Math.Min(width, height) / DocumentGeometryRules.SmallestCardSideDivisor;
            var minLine = DocumentGeometryRules.SideSupportMin * smallest;
            var rows = LinePositions(rowEdges, minLine);
            var columns = LinePositions(columnEdges, minLine);
            if (rows.Count == 0 || columns.Count == 0)
                return null;

            rows.Insert(0, 0);
            rows.Add(height - 1);
            columns.Insert(0, 0);
            columns.Add(width - 1);
            rowPrefix = RowPrefix(horizontal, width, height);
            columnPrefix = ColumnPrefix(vertical, width, height);
            return BestRectangle(width, height, rowPrefix, columnPrefix, rows, columns, layout, smallest);
        }
        finally
        {
            Wipe(gradientX);
            Wipe(gradientY);
            Wipe(horizontal);
            Wipe(vertical);
            Wipe(rowEdges);
            Wipe(columnEdges);
            Wipe(rowPrefix);
            Wipe(columnPrefix);
        }
    }

    private static void Wipe(Array? array)
    {
        if (array is not null)
            Array.Clear(array);
    }

    /// <summary>
    /// The Sobel gradient of the interior of the picture into <paramref name="gradientX"/> and <paramref name="gradientY"/> (the border row and column stay 0), and the value
    /// of <c>|gx| + |gy|</c> from which a pixel is an edge: the larger of <see cref="DocumentGeometryRules.EdgeFloor"/> and <see cref="DocumentGeometryRules.EdgeRelative"/> of the 99th percentile of the picture.
    /// </summary>
    private static int Gradients(GrayBitmap gray, short[] gradientX, short[] gradientY)
    {
        var width = gray.Width;
        var height = gray.Height;
        var pixels = gray.Pixels;
        var histogram = new int[MaxGradient + 1];
        var total = 0;
        for (var y = 1; y < height - 1; y++)
        {
            var above = (y - 1) * width;
            var here = y * width;
            var below = (y + 1) * width;
            for (var x = 1; x < width - 1; x++)
            {
                var across = pixels[above + x + 1] + (2 * pixels[here + x + 1]) + pixels[below + x + 1]
                    - pixels[above + x - 1] - (2 * pixels[here + x - 1]) - pixels[below + x - 1];
                var down = pixels[below + x - 1] + (2 * pixels[below + x]) + pixels[below + x + 1]
                    - pixels[above + x - 1] - (2 * pixels[above + x]) - pixels[above + x + 1];
                gradientX[here + x] = (short)across;
                gradientY[here + x] = (short)down;
                histogram[Math.Abs(across) + Math.Abs(down)]++;
                total++;
            }
        }

        var target = (int)Math.Ceiling(total * DocumentGeometryRules.PercentileShare);
        var running = 0;
        var percentile = 0;
        for (var magnitude = 0; magnitude < histogram.Length; magnitude++)
        {
            running += histogram[magnitude];
            if (running >= target)
            {
                percentile = magnitude;
                break;
            }
        }

        return Math.Max(DocumentGeometryRules.EdgeFloor, (int)Math.Round(DocumentGeometryRules.EdgeRelative * percentile));
    }

    /// <summary>
    /// The rows (or columns) where a long straight edge may be: the peaks of the profile of <paramref name="counts"/> (the edge pixels of every row or column, summed with its two
    /// neighbours since an edge is a pixel or two wide) that reach <paramref name="minimum"/> and are the highest within <see cref="DocumentGeometryRules.BandRadius"/>, the
    /// <see cref="DocumentGeometryRules.MaxLineCandidates"/> strongest, in order.
    /// </summary>
    private static List<int> LinePositions(int[] counts, double minimum)
    {
        var size = counts.Length;
        var smooth = new int[size];
        for (var i = 0; i < size; i++)
            smooth[i] = (i > 0 ? counts[i - 1] : 0) + counts[i] + (i < size - 1 ? counts[i + 1] : 0);

        var peaks = new List<(int Position, int Score)>();
        for (var i = 1; i < size - 1; i++)
        {
            var score = smooth[i];
            if (score < minimum)
                continue;

            var isPeak = true;
            for (var near = Math.Max(0, i - DocumentGeometryRules.BandRadius); near <= Math.Min(size - 1, i + DocumentGeometryRules.BandRadius) && isPeak; near++)
            {
                if (smooth[near] > score || (smooth[near] == score && near < i))
                    isPeak = false;
            }

            if (isPeak)
                peaks.Add((i, score));
        }

        return peaks
            .OrderByDescending(peak => peak.Score)
            .ThenBy(peak => peak.Position)
            .Take(DocumentGeometryRules.MaxLineCandidates)
            .Select(peak => peak.Position)
            .Order()
            .ToList();
    }

    /// <summary>
    /// For every row, the running count of its edge pixels: <c>prefix[y * (width + 1) + x + 1]</c> is the count in the columns 0 to x of row y, so the edge pixels of a stretch
    /// of a row are a difference of two entries.
    /// </summary>
    private static int[] RowPrefix(byte[] edges, int width, int height)
    {
        var prefix = new int[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width + 1);
            for (var x = 0; x < width; x++)
                prefix[row + x + 1] = prefix[row + x] + edges[(y * width) + x];
        }

        return prefix;
    }

    /// <summary>The same for the columns: <c>prefix[x * (height + 1) + y + 1]</c> is the count of the edge pixels in the rows 0 to y of column x.</summary>
    private static int[] ColumnPrefix(byte[] edges, int width, int height)
    {
        var prefix = new int[width * (height + 1)];
        for (var x = 0; x < width; x++)
        {
            var column = x * (height + 1);
            for (var y = 0; y < height; y++)
                prefix[column + y + 1] = prefix[column + y] + edges[(y * width) + x];
        }

        return prefix;
    }

    /// <summary>
    /// The share of columns <paramref name="from"/> to <paramref name="to"/> that are edge pixels in the row that has the most of them within <see cref="DocumentGeometryRules.BandRadius"/> of the row
    /// <paramref name="y"/>: how much of a straight horizontal edge the row has there.
    /// </summary>
    private static double RowSupport(int[] rowPrefix, int width, int height, int y, int from, int to)
    {
        var best = 0;
        for (var near = Math.Max(0, y - DocumentGeometryRules.BandRadius); near <= Math.Min(height - 1, y + DocumentGeometryRules.BandRadius); near++)
            best = Math.Max(best, rowPrefix[(near * (width + 1)) + to + 1] - rowPrefix[(near * (width + 1)) + from]);

        return (double)best / (to - from + 1);
    }

    /// <summary>The share of rows <paramref name="from"/> to <paramref name="to"/> that are edge pixels in the column that has the most of them within <see cref="DocumentGeometryRules.BandRadius"/> of the column <paramref name="x"/>.</summary>
    private static double ColumnSupport(int[] columnPrefix, int width, int height, int x, int from, int to)
    {
        var best = 0;
        for (var near = Math.Max(0, x - DocumentGeometryRules.BandRadius); near <= Math.Min(width - 1, x + DocumentGeometryRules.BandRadius); near++)
            best = Math.Max(best, columnPrefix[(near * (height + 1)) + to + 1] - columnPrefix[(near * (height + 1)) + from]);

        return (double)best / (to - from + 1);
    }

    /// <summary>
    /// The best rectangle bounded by two of <paramref name="rows"/> and two of <paramref name="columns"/> (the first and the last of each are the border of the picture, a
    /// side that runs out of the frame), or null. A side that is not the border must be a straight edge along <see cref="DocumentGeometryRules.SideSupportMin"/> of its length; the rectangle needs a
    /// horizontal and a vertical side that are lines of the picture. See <see cref="Rank"/> for what decides.
    /// </summary>
    private static CardRectangle? BestRectangle(
        int width, int height, int[] rowPrefix, int[] columnPrefix, List<int> rows, List<int> columns, DocumentFormatLayout layout, double minExtent)
    {
        CardRectangle? best = null;
        var bestRank = default(Rank);
        var minArea = layout.MinCardAreaShare * width * height;
        for (var a = 0; a < rows.Count; a++)
        {
            for (var b = a + 1; b < rows.Count; b++)
            {
                var top = rows[a];
                var bottom = rows[b];
                if (bottom - top < minExtent)
                    continue;

                for (var c = 0; c < columns.Count; c++)
                {
                    for (var d = c + 1; d < columns.Count; d++)
                    {
                        var left = columns[c];
                        var right = columns[d];
                        if (right - left < minExtent)
                            continue;

                        var topSeen = top != 0;
                        var bottomSeen = bottom != height - 1;
                        var leftSeen = left != 0;
                        var rightSeen = right != width - 1;
                        if ((!topSeen && !bottomSeen) || (!leftSeen && !rightSeen))
                            continue;

                        if ((topSeen && RowSupport(rowPrefix, width, height, top, left, right) < DocumentGeometryRules.SideSupportMin)
                            || (bottomSeen && RowSupport(rowPrefix, width, height, bottom, left, right) < DocumentGeometryRules.SideSupportMin)
                            || (leftSeen && ColumnSupport(columnPrefix, width, height, left, top, bottom) < DocumentGeometryRules.SideSupportMin)
                            || (rightSeen && ColumnSupport(columnPrefix, width, height, right, top, bottom) < DocumentGeometryRules.SideSupportMin))
                        {
                            continue;
                        }

                        var candidate = new CardRectangle(left, top, right, bottom, leftSeen, topSeen, rightSeen, bottomSeen);
                        var area = (long)(right - left) * (bottom - top);
                        var aspectMatch = candidate.SeenSides == 4
                            && Math.Abs(LongOverShort(right - left, bottom - top) - layout.AspectRatio) <= layout.AspectTolerance;
                        var rank = new Rank(area >= minArea, candidate.SeenSides, aspectMatch, area);
                        if (best is null || IsBetter(rank, bestRank))
                        {
                            best = candidate;
                            bestRank = rank;
                        }
                    }
                }
            }
        }

        return best;
    }

    private static bool IsBetter(Rank candidate, Rank best)
    {
        if (candidate.Plausible != best.Plausible)
            return candidate.Plausible;

        if (candidate.SeenSides != best.SeenSides)
            return candidate.SeenSides > best.SeenSides;

        if (candidate.AspectMatch != best.AspectMatch)
            return candidate.AspectMatch;

        return candidate.Area > best.Area;
    }
}
