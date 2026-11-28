using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// Says whether a photo shows the WHOLE card of the expected format, upright, with its edges visible and its portrait where the layout puts it (E71/F01/US01/T01,
/// NF-189, decisions D1 and D5), also when the card is not held straight (IDENT-TILT). Pure and managed: no new package, no native code, no file, no network, no logger, no state;
/// the picture is the caller's and is not touched, and every copy made here is zeroed before returning (ADR-013-R2) but one: the levelled card a report hands on
/// (<see cref="DocumentGeometryReport.RectifiedCard"/>), which is the report's and is zeroed when its owner disposes it. The inspector decides nothing but the codes of
/// <see cref="IdentityGuidanceCodes"/>: the flow turns them into guidance (E71/F01/US03/T01), the policy and the format file hold every number of a decision.
/// <para>
/// <b>Algorithm.</b> (1) A gray copy of at most <see cref="CaptureQualityRules.StatisticsSide"/> pixels (the one <c>CaptureQualityAssessor</c> uses; the picture already
/// has its EXIF orientation and its pixel limit, <c>RasterImage.Decode</c>). (2) The Sobel gradient; the strong pixels whose gradient is mostly vertical are the pixels
/// of horizontal edges, the ones whose gradient is mostly horizontal are the pixels of vertical edges. (3) The projection profiles of those two maps (the edge pixels per
/// row and per column) give the rows and the columns where a long straight edge may be; the border of the picture is added to each list, for a card that runs out of the
/// frame. (4) The card is the largest rectangle bounded by such rows and columns whose sides are straight edges: a side is a line of the picture when one row (or column)
/// within a pixel or two of it has an edge pixel along three quarters of the side, so a structure inside the card (the portrait is 62% of its height) is not a side. Of the
/// rectangles that cover at least <c>Layout.MinCardAreaShare</c>, the one with the most sides that are lines of the picture (a side that is the border of the picture is a card
/// cut by the frame), then the one of the ratio of the format, then the largest.
/// (5) <b>A card that is not held straight</b> (IDENT-TILT). A side is a line of the picture only to within about a fifth of a degree of the rows and the columns (a card was read whole
/// up to 0.2 degrees either way and not found from 0.35 in <c>geometry-2026.11-1</c>), so when the picture as it lies has no whole, plausible card of the ratio of the format the angle of
/// the card is looked for (<see cref="DocumentTilt"/>): the strong edge pixels are turned by every angle up to <see cref="DocumentGeometryRules.TiltSearchDegrees"/> either way, a coarse
/// step at a time, and how sharply they pile up in rows and in columns scores the angle (the edge support of (3) and (4) summed over the picture: the sides of a card and the lines of its
/// text all fall in one row or one column at one angle, and nothing short weighs as they do); the <see cref="DocumentGeometryRules.TiltCandidates"/> best peaks that stand out are refined
/// by a fine scan; and for each the gray copy is levelled by the angle and (4) is run on the levelled copy, a card being the whole rectangle found there. The winner is the best by the rank of
/// (4) and then by being within <see cref="DocumentGeometryRules.MaxTiltDegrees"/>: a whole card of the ratio replaces what was found as the picture lies, a card whole and plausible but of
/// another ratio only replaces a card that was not whole. A picture whose card is supported as it lies (whole, plausible, of the ratio) is not searched at all: the common case costs what it did
/// and <see cref="DocumentGeometryReport.TiltDegrees"/> is 0. A card turned by up to <see cref="DocumentGeometryRules.MaxTiltDegrees"/> either way is read; a card found turned by more
/// (up to the search) has <c>EdgesVisible</c> false, so the guest is asked to hold it straighter (<c>document_edges_not_visible</c>) and nothing of it is read. The box of a turned card is
/// the box of its four corners in the picture, which keep <c>Layout.MarginMin</c> from the border; its ratio and its area are the ones of the card, not of that box.
/// (6) <see cref="DocumentGeometryReport.EdgesVisible"/>, <see cref="DocumentGeometryReport.AspectOk"/> and the rotation of 0 or 90 degrees follow from the rectangle and <c>Layout</c>.
/// (7) On a measurable card (whole, visible, of the ratio) the portrait is looked for with <c>DocumentPortrait</c>, which tries the picture upright and
/// turned by 90, 270 and 180 degrees and keeps the turn at which the detector is surest; for a turned card it is looked for in the card levelled, so that the zone of the portrait is where the
/// layout puts it. The turn of a side that carries the MRZ band and no portrait (the back) comes from the
/// band (<c>Layout.Sides[side].MrzRegion</c>: the band is the densest text of a document, so the card is upright when the band has more edges than the opposite region) BEFORE
/// any face: a face found on such a side is a front sent as the back or a stray detection, and only says where a portrait is (for the classifier of the side), never how the
/// card lies. On any other side the turn is the one at which the face is found, and a card with no face to tell it takes its turn from the band of the side.
/// (8) Glare and blur are measured with the statistics of the capture checks, on the card. (9) A measurable card turned by more than <see cref="DocumentGeometryRules.TiltRoundingStepDegrees"/>
/// is handed on levelled and turned upright, in memory only, as <see cref="DocumentGeometryReport.RectifiedCard"/>: sampled in the picture at its own resolution in one pass (no levelled copy of
/// the whole picture is made), it is what the zones of the format, the MRZ band and the portrait of the document step are read on; a card that lies straight is cut as it lies, with no copy made here.
/// <b>Measured</b> (<c>DocumentGeometryInspectorTests</c>: the card of <c>DocumentImageFactory</c>, 480 by 303 pixels in a picture of 640 by 480, its edges softened by a bilinear turn as in a photo;
/// the front and the back, upright, upside down and turned a quarter either way): the card is found whole at every angle from 0.25 to 10 degrees either way and its angle is known to 0.1 degree;
/// at 12, 15 and 20 degrees it is found and refused for its turn (<c>document_edges_not_visible</c>); at 25 degrees and more nothing is looked for (<c>document_not_found</c>, as before). The
/// negative pictures (no card, a rectangle of another ratio, a card too dim for an edge, a page of text, a card cut by the frame) are refused with the codes they had. Real photos may not behave
/// the same: the turn is in the plane of the picture only (a card seen in perspective is not levelled), and the numbers are provisional until the qualification on real photos (D12).
/// </para>
/// <para>
/// <b>Numbers.</b> The numbers of the layout come from the format file (<c>aspectRatio</c>, <c>aspectTolerance</c>, <c>minCardAreaShare</c>, <c>marginMin</c> and the portrait
/// zone), the score of the face detector from <c>Biometrics:DocumentDetectionScoreThreshold</c>, the limits of glare and blur from <see cref="CaptureQualityRules"/>.
/// The parameters of the edge detector (how strong an edge is, how much of a side must be an edge for it to be a line of the picture, how small a card is still looked for) and of the
/// search of the angle (how much turn a card may have, how far it is looked for, the steps and the candidates) are <see cref="DocumentGeometryRules"/>. Several of them decide whether a card is
/// whole or found at all, so the whole set is versioned (<see cref="DocumentGeometryRules.Version"/>, carried by every report as
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
    private const int MaxGradient = 2040;

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
        using var finding = FindCard(gray, layout, cancellationToken);
        if (finding.Card is not { } card)
        {
            var (wholeGlare, wholeBlur) = Quality(gray);
            return new DocumentGeometryReport(
                false, null, false, 0, false, 0, null, wholeGlare, wholeBlur, QualityCodes([IdentityGuidanceCodes.DocumentNotFound], wholeGlare, wholeBlur),
                DocumentGeometryRules.Version);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tilt = finding.TiltDegrees;
        var seen = finding.Levelled ?? gray;
        NormalizedBox box;
        double cardWidth;
        double cardHeight;
        double areaShare;
        bool margins;
        if (tilt == 0)
        {
            box = ToBox(card, gray.Width, gray.Height);
            cardWidth = box.W * image.Width;
            cardHeight = box.H * image.Height;
            areaShare = box.W * box.H;
            margins = box.X >= layout.MarginMin && box.Y >= layout.MarginMin
                && 1 - (box.X + box.W) >= layout.MarginMin && 1 - (box.Y + box.H) >= layout.MarginMin;
        }
        else
        {
            (box, margins) = BoxOfTurnedCard(card, tilt, gray.Width, gray.Height, layout.MarginMin);
            cardWidth = (double)(card.Right - card.Left) / gray.Width * image.Width;
            cardHeight = (double)(card.Bottom - card.Top) / gray.Height * image.Height;
            areaShare = (double)(card.Right - card.Left) / gray.Width * ((double)(card.Bottom - card.Top) / gray.Height);
        }

        var ratio = LongOverShort(cardWidth, cardHeight);
        var allSidesSeen = card.SeenSides == 4;
        var aspectOk = allSidesSeen && Math.Abs(ratio - layout.AspectRatio) <= layout.AspectTolerance;
        var withinLimit = Math.Abs(tilt) <= DocumentGeometryRules.MaxTiltDegrees;
        var edgesVisible = allSidesSeen && margins && areaShare >= layout.MinCardAreaShare && withinLimit;

        using var cardGray = seen.Cropped(card.LeftEdge, card.TopEdge, card.RightEdge(seen.Width) - card.LeftEdge + 1, card.BottomEdge(seen.Height) - card.TopEdge + 1);
        var (glare, blur) = Quality(cardGray ?? seen);

        var orientation = new Orientation(0, true, null, false);
        RasterImage? levelCard = null;
        RasterImage? rectified = null;
        try
        {
            if (edgesVisible && aspectOk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tall = cardHeight > cardWidth;

                if (Math.Abs(tilt) > DocumentGeometryRules.TiltRoundingStepDegrees)
                    levelCard = DocumentTilt.LevelCard(image, seen.Width, seen.Height, tilt, card.Left, card.Top, card.Right, card.Bottom);

                orientation = ResolveOrientation(image, levelCard, cardGray, box, tall, format, declaredSide, lookForPortrait, cancellationToken);
                if (levelCard is not null && orientation.Decided)
                {
                    var exif = DocumentGeometryReport.ExifOrientationOf(orientation.Rotation);
                    rectified = exif == 1 ? levelCard : levelCard.Oriented(exif);
                }
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

            var report = new DocumentGeometryReport(
                true, box, edgesVisible, ratio, aspectOk, orientation.Rotation, orientation.PortraitInZone, glare, blur, QualityCodes(codes, glare, blur),
                DocumentGeometryRules.Version, tilt, rectified);

            if (ReferenceEquals(levelCard, rectified))
                levelCard = null;

            rectified = null;
            return report;
        }
        finally
        {
            levelCard?.Dispose();
            rectified?.Dispose();
        }
    }

    private sealed record CardRectangle(int Left, int Top, int Right, int Bottom, bool LeftSeen, bool TopSeen, bool RightSeen, bool BottomSeen)
    {
        public int SeenSides => (LeftSeen ? 1 : 0) + (TopSeen ? 1 : 0) + (RightSeen ? 1 : 0) + (BottomSeen ? 1 : 0);

        public int LeftEdge => LeftSeen ? Left : 0;

        public int TopEdge => TopSeen ? Top : 0;

        public int RightEdge(int width) => RightSeen ? Right : width - 1;

        public int BottomEdge(int height) => BottomSeen ? Bottom : height - 1;
    }

    private sealed class CardFinding(CardRectangle? card, double tiltDegrees, GrayBitmap? levelled) : IDisposable
    {
        public CardRectangle? Card { get; } = card;

        public double TiltDegrees { get; } = tiltDegrees;

        public GrayBitmap? Levelled { get; } = levelled;

        public void Dispose() => Levelled?.Dispose();
    }

    private readonly record struct Rank(bool Plausible, int SeenSides, bool WithinLimit, bool AspectMatch, long Area);

    private sealed record Orientation(int Rotation, bool Decided, bool? PortraitInZone, bool PortraitFarOutside);

    private sealed record PortraitEvidence(int ExifOrientation, double CenterX, double CenterY, double Left, double Top, double Right, double Bottom);

    private static NormalizedBox ToBox(CardRectangle card, int width, int height)
    {
        var left = card.LeftEdge;
        var top = card.TopEdge;
        var right = card.RightSeen ? card.Right : width;
        var bottom = card.BottomSeen ? card.Bottom : height;
        return new NormalizedBox((double)left / width, (double)top / height, (double)(right - left) / width, (double)(bottom - top) / height);
    }

    private static (NormalizedBox Box, bool MarginsKept) BoxOfTurnedCard(CardRectangle card, double degrees, int width, int height, double marginMin)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        foreach (var (x, y) in new[] { (card.Left, card.Top), (card.Right, card.Top), (card.Right, card.Bottom), (card.Left, card.Bottom) })
        {
            var (pictureX, pictureY) = DocumentTilt.ToPicture(degrees, width, height, x, y);
            minX = Math.Min(minX, pictureX);
            minY = Math.Min(minY, pictureY);
            maxX = Math.Max(maxX, pictureX);
            maxY = Math.Max(maxY, pictureY);
        }

        var left = minX / width;
        var top = minY / height;
        var right = maxX / width;
        var bottom = maxY / height;
        var kept = left >= marginMin && top >= marginMin && 1 - right >= marginMin && 1 - bottom >= marginMin;
        var cutLeft = Math.Clamp(left, 0, 1);
        var cutTop = Math.Clamp(top, 0, 1);
        return (new NormalizedBox(cutLeft, cutTop, Math.Clamp(right, 0, 1) - cutLeft, Math.Clamp(bottom, 0, 1) - cutTop), kept);
    }

    private static double LongOverShort(double width, double height)
    {
        var shortest = Math.Min(width, height);
        return shortest <= 0 ? 0 : Math.Max(width, height) / shortest;
    }

    private static (double Glare, double Blur) Quality(GrayBitmap region) =>
        (MrzImageLocator.SaturatedShare(region), MrzImageLocator.BlurRatio(region));

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
    /// neither, a landscape card is taken as upright and a tall one is not decided. <paramref name="levelCard"/> is the card levelled, as it lies, when it was turned (the face is looked
    /// for in it); null for a card that lies straight, whose face is looked for in the box cut out of the picture.
    /// </summary>
    private Orientation ResolveOrientation(
        RasterImage image,
        RasterImage? levelCard,
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

        var evidence = zone is null || !lookForPortrait ? null : FindPortrait(image, levelCard, box, tall, cancellationToken);
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

    private PortraitEvidence? FindPortrait(RasterImage image, RasterImage? levelCard, NormalizedBox box, bool tall, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var crop = levelCard is null ? CropCard(image, box, settings.DetectorInputSize) : ShrunkCopy(levelCard, settings.DetectorInputSize);
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

    private static RasterImage ShrunkCopy(RasterImage card, int longestSide)
    {
        var longest = Math.Max(card.Width, card.Height);
        if (longest <= longestSide)
            return card.Cropped(0, 0, card.Width, card.Height);

        var scale = (double)longestSide / longest;
        return card.Resized(Math.Max(1, (int)Math.Round(card.Width * scale)), Math.Max(1, (int)Math.Round(card.Height * scale)));
    }

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

    private static bool Overlaps(DocumentFormatRegion zone, PortraitEvidence face) =>
        face.Left < zone.X + zone.W && face.Right > zone.X && face.Top < zone.Y + zone.H && face.Bottom > zone.Y;

    private static CardFinding FindCard(GrayBitmap gray, DocumentFormatLayout layout, CancellationToken cancellationToken)
    {
        using var edges = ComputeEdges(gray, cancellationToken);
        if (edges is null)
            return new CardFinding(null, 0, null);

        var straight = FindCard(edges, layout, cancellationToken);
        if (IsSupported(straight, gray, layout))
            return new CardFinding(straight, 0, null);

        var turned = FindTurnedCard(gray, edges, layout, straight, cancellationToken);
        return turned ?? new CardFinding(straight, 0, null);
    }

    private static bool IsSupported(CardRectangle? card, GrayBitmap gray, DocumentFormatLayout layout)
    {
        if (card is null || card.SeenSides != 4)
            return false;

        var rank = RankOf(card, gray.Width, gray.Height, layout, withinLimit: true);
        return rank.Plausible && rank.AspectMatch;
    }

    private static CardFinding? FindTurnedCard(
        GrayBitmap gray, DocumentEdgeMaps edges, DocumentFormatLayout layout, CardRectangle? straight, CancellationToken cancellationToken)
    {
        CardFinding? best = null;
        var bestRank = default(Rank);
        using var points = DocumentEdgePoints.From(edges);
        try
        {
            var angles = DocumentTilt.Angles(points, DocumentGeometryRules.TiltSearchDegrees, DocumentGeometryRules.TiltCandidates);
            foreach (var angle in angles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Math.Abs(angle) < DocumentGeometryRules.TiltRoundingStepDegrees / 2)
                    continue;

                var found = LevelAndFind(gray, angle, layout, cancellationToken);
                if (found?.Card is not { } card)
                    continue;

                var rank = RankOf(card, gray.Width, gray.Height, layout, Math.Abs(found.TiltDegrees) <= DocumentGeometryRules.MaxTiltDegrees);
                if (best is null || IsBetter(rank, bestRank))
                {
                    best?.Dispose();
                    best = found;
                    bestRank = rank;
                }
                else
                {
                    found.Dispose();
                }

                if (bestRank.Plausible && bestRank.WithinLimit && bestRank.AspectMatch)
                    break;
            }

            if (best is null)
                return null;

            if (straight is not null && !IsBetter(bestRank, RankOf(straight, gray.Width, gray.Height, layout, withinLimit: true)))
                return null;

            var winner = best;
            best = null;
            return winner;
        }
        finally
        {
            best?.Dispose();
        }
    }

    private static CardFinding? LevelAndFind(GrayBitmap gray, double degrees, DocumentFormatLayout layout, CancellationToken cancellationToken)
    {
        GrayBitmap? levelled = null;
        DocumentEdgeMaps? edges = null;
        try
        {
            levelled = DocumentTilt.Level(gray, degrees);
            edges = ComputeEdges(levelled, cancellationToken);
            if (edges is null)
                return null;

            var card = FindCard(edges, layout, cancellationToken);
            if (card is null || card.SeenSides != 4)
                return null;

            var finding = new CardFinding(card, degrees, levelled);
            levelled = null;
            return finding;
        }
        finally
        {
            edges?.Dispose();
            levelled?.Dispose();
        }
    }

    private static DocumentEdgeMaps? ComputeEdges(GrayBitmap gray, CancellationToken cancellationToken)
    {
        var width = gray.Width;
        var height = gray.Height;
        if (width < DocumentGeometryRules.MinWorkingSide || height < DocumentGeometryRules.MinWorkingSide)
            return null;

        var edges = new DocumentEdgeMaps(width, height);
        short[]? gradientX = null;
        short[]? gradientY = null;
        try
        {
            gradientX = DocumentTilt.Track(new short[width * height]);
            gradientY = DocumentTilt.Track(new short[width * height]);
            var threshold = Gradients(gray, gradientX, gradientY);
            cancellationToken.ThrowIfCancellationRequested();

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
                        edges.Horizontal[at] = 1;
                        edges.RowEdges[y]++;
                    }
                    else if (across >= DocumentGeometryRules.DominanceFactor * down)
                    {
                        edges.Vertical[at] = 1;
                        edges.ColumnEdges[x]++;
                    }
                }
            }

            return edges;
        }
        catch
        {
            edges.Dispose();
            throw;
        }
        finally
        {
            Wipe(gradientX);
            Wipe(gradientY);
        }
    }

    private static CardRectangle? FindCard(DocumentEdgeMaps edges, DocumentFormatLayout layout, CancellationToken cancellationToken)
    {
        var width = edges.Width;
        var height = edges.Height;
        int[]? rowPrefix = null;
        int[]? columnPrefix = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var smallest = (double)Math.Min(width, height) / DocumentGeometryRules.SmallestCardSideDivisor;
            var minLine = DocumentGeometryRules.SideSupportMin * smallest;
            var rows = LinePositions(edges.RowEdges, minLine);
            var columns = LinePositions(edges.ColumnEdges, minLine);
            if (rows.Count == 0 || columns.Count == 0)
                return null;

            rows.Insert(0, 0);
            rows.Add(height - 1);
            columns.Insert(0, 0);
            columns.Add(width - 1);
            rowPrefix = DocumentTilt.Track(RowPrefix(edges.Horizontal, width, height));
            columnPrefix = DocumentTilt.Track(ColumnPrefix(edges.Vertical, width, height));
            return BestRectangle(width, height, rowPrefix, columnPrefix, rows, columns, layout, smallest);
        }
        finally
        {
            Wipe(rowPrefix);
            Wipe(columnPrefix);
        }
    }

    private static void Wipe(Array? array)
    {
        if (array is not null)
            Array.Clear(array);
    }

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

    private static double RowSupport(int[] rowPrefix, int width, int height, int y, int from, int to)
    {
        var best = 0;
        for (var near = Math.Max(0, y - DocumentGeometryRules.BandRadius); near <= Math.Min(height - 1, y + DocumentGeometryRules.BandRadius); near++)
            best = Math.Max(best, rowPrefix[(near * (width + 1)) + to + 1] - rowPrefix[(near * (width + 1)) + from]);

        return (double)best / (to - from + 1);
    }

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
                        var rank = RankOf(candidate, width, height, layout, withinLimit: true);
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

    private static Rank RankOf(CardRectangle card, int width, int height, DocumentFormatLayout layout, bool withinLimit)
    {
        var across = card.Right - card.Left;
        var down = card.Bottom - card.Top;
        var area = (long)across * down;
        var aspectMatch = card.SeenSides == 4 && Math.Abs(LongOverShort(across, down) - layout.AspectRatio) <= layout.AspectTolerance;
        return new Rank(area >= layout.MinCardAreaShare * width * height, card.SeenSides, withinLimit, aspectMatch, area);
    }

    private static bool IsBetter(Rank candidate, Rank best)
    {
        if (candidate.Plausible != best.Plausible)
            return candidate.Plausible;

        if (candidate.SeenSides != best.SeenSides)
            return candidate.SeenSides > best.SeenSides;

        if (candidate.WithinLimit != best.WithinLimit)
            return candidate.WithinLimit;

        if (candidate.AspectMatch != best.AspectMatch)
            return candidate.AspectMatch;

        return candidate.Area > best.Area;
    }
}
