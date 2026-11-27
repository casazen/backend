using System.Diagnostics;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US01/T01 (NF-189, D1, D5): the inspector of the geometry of the document. Every picture is built in code (<see cref="DocumentImageFactory"/>): a whole card
/// inside the frame is found with its edges visible and the ratio of the format; a card cut by the frame, too close to the border, too small or of another ratio is told
/// apart; a card turned by a quarter or a half turn is reported with the turn that makes it upright; the portrait is looked for with the face detector (scripted, or the
/// one that finds the stand-in of the factory) and glare and blur are the statistics and the limits of the capture checks. The format is the Italian card as the registry
/// loads it from its file. A card that is not held straight (IDENT-TILT) is found at its angle, up to ten degrees either way, and handed on levelled; a card held more crooked is found and
/// refused for it; the pictures that are no card of the format are refused with the codes they had; every copy made of the picture is zeroed (a seam of the search records them).
/// </summary>
public class DocumentGeometryInspectorTests
{
    private static readonly DocumentFormat Cie = new DocumentFormatRegistry(
        Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, AppContext.BaseDirectory).Get("it-cie-3.0")!;

    private static readonly BiometricModelOptions Models = new();

    private static DocumentGeometryInspector Inspector(IFaceDetector detector, BiometricModelOptions? options = null) =>
        new(detector, Options.Create(options ?? Models));

    private static DocumentGeometryReport Inspect(RasterImage image, IFaceDetector detector, DocumentSide side = DocumentSide.Front, CancellationToken cancellationToken = default)
    {
        return Inspector(detector).Inspect(image, Cie, side, cancellationToken);
    }

    /// <summary>A scripted detector that answers the upright picture with a face at the given shares of a card of <paramref name="cardWidth"/> by <paramref name="cardHeight"/> pixels, and the three turned ones with none.</summary>
    private static ScriptedFaceDetector FaceAt(DocumentFormatRegion share, int cardWidth, int cardHeight) =>
        new(OnCard(share, cardWidth, cardHeight), [], [], []);

    /// <summary>One face of the card, as the detector answers: its box is the <paramref name="share"/> of a card of <paramref name="cardWidth"/> by <paramref name="cardHeight"/> pixels.</summary>
    private static IReadOnlyList<DetectedFace> OnCard(DocumentFormatRegion share, int cardWidth, int cardHeight) =>
    [
        new DetectedFace(
            (float)(share.X * cardWidth), (float)(share.Y * cardHeight), (float)(share.W * cardWidth), (float)(share.H * cardHeight), 0.9f, new float[10], share.W * share.H),
    ];

    /// <summary>The middle of the portrait zone of the card (0.03, 0.22, 0.3, 0.62) as a face a bit smaller than the zone.</summary>
    private static DocumentFormatRegion InTheZone => new(0.06, 0.3, 0.24, 0.45);

    // ---- AC1: a whole card is found, a card cut by the frame is not whole -----------------------------------------------------------------------------------------

    [Fact]
    public void Inspect_AWholeCardInsideTheFrame_IsFoundWithVisibleEdgesAndTheExpectedAspect()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var detector = new PortraitStandInDetector();

        var report = Inspect(picture, detector);

        Assert.True(report.CardFound);
        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.Equal(0, report.RotationDegrees);
        Assert.Equal(1, report.ExifOrientation);
        Assert.True(report.PortraitInZone);
        Assert.Empty(report.Codes);
        Assert.InRange(report.AspectRatio, Cie.Layout.AspectRatio - Cie.Layout.AspectTolerance, Cie.Layout.AspectRatio + Cie.Layout.AspectTolerance);
        var card = DocumentImageFactory.WholeCard;
        var box = Assert.IsType<NormalizedBox>(report.CardBox);
        AssertNear((double)card.Left / DocumentImageFactory.Width, box.X);
        AssertNear((double)card.Top / DocumentImageFactory.Height, box.Y);
        AssertNear((double)card.Width / DocumentImageFactory.Width, box.W);
        AssertNear((double)card.Height / DocumentImageFactory.Height, box.H);
    }

    [Theory]
    [InlineData(-100, 70, "left")]
    [InlineData(260, 70, "right")]
    [InlineData(80, -60, "top")]
    [InlineData(80, 250, "bottom")]
    public void Inspect_ACardCutByTheFrame_IsFoundButItsEdgesAreNotVisible(int left, int top, string cutSide)
    {
        Assert.NotEmpty(cutSide);
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(left, top, 480, 303));
        var detector = new ScriptedFaceDetector();

        var report = Inspect(picture, detector);

        Assert.True(report.CardFound);
        Assert.False(report.EdgesVisible);
        Assert.False(report.AspectOk);
        Assert.Equal([IdentityGuidanceCodes.DocumentEdgesNotVisible], report.Codes);
        Assert.Null(report.PortraitInZone);
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public void Inspect_ACardCloserToTheBorderThanTheMarginOfTheFormat_HasItsEdgesNotVisible()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(3, 70, 480, 303));

        var report = Inspect(picture, new ScriptedFaceDetector());

        Assert.True(report.CardFound);
        Assert.False(report.EdgesVisible);
        Assert.True(Cie.Layout.MarginMin > 3.0 / DocumentImageFactory.Width);
        Assert.Equal([IdentityGuidanceCodes.DocumentEdgesNotVisible], report.Codes);
    }

    [Fact]
    public void Inspect_ACardThatCoversLessThanTheAreaTheFormatAsks_HasItsEdgesNotVisible()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(200, 170, 240, 151));

        var report = Inspect(picture, new ScriptedFaceDetector());

        var box = Assert.IsType<NormalizedBox>(report.CardBox);
        Assert.True(box.W * box.H < Cie.Layout.MinCardAreaShare);
        Assert.False(report.EdgesVisible);
        Assert.Equal([IdentityGuidanceCodes.DocumentEdgesNotVisible], report.Codes);
    }

    [Fact]
    public void Inspect_APictureWithNoCard_IsNotFound()
    {
        using var picture = DocumentImageFactory.Plain(120);

        var report = Inspect(picture, new ScriptedFaceDetector());

        Assert.False(report.CardFound);
        Assert.Null(report.CardBox);
        Assert.False(report.EdgesVisible);
        Assert.False(report.AspectOk);
        Assert.Equal(0, report.AspectRatio);
        Assert.Equal(0, report.RotationDegrees);
        Assert.Null(report.PortraitInZone);
        Assert.Contains(IdentityGuidanceCodes.DocumentNotFound, report.Codes);
        Assert.Equal(IdentityGuidanceCodes.DocumentNotFound, report.Codes[0]);
    }

    // ---- AC2: another ratio is a layout mismatch, a quarter or a half turn is reported -------------------------------------------------------------------------------

    [Fact]
    public void Inspect_ACardOfAnotherAspectRatio_IsALayoutMismatch()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(100, 80, 400, 320));
        var detector = new ScriptedFaceDetector();

        var report = Inspect(picture, detector);

        Assert.True(report.CardFound);
        Assert.True(report.EdgesVisible);
        Assert.False(report.AspectOk);
        Assert.True(Math.Abs(report.AspectRatio - Cie.Layout.AspectRatio) > Cie.Layout.AspectTolerance);
        Assert.Equal([IdentityGuidanceCodes.DocumentLayoutMismatch], report.Codes);
        Assert.Null(report.PortraitInZone);
        Assert.Equal(0, detector.Calls);
    }

    [Theory]
    [InlineData(1.55, true)]
    [InlineData(1.62, true)]
    [InlineData(1.40, false)]
    [InlineData(1.75, false)]
    public void Inspect_TheToleranceOfTheAspectRatio_IsTheOneOfTheFormatFile(double ratio, bool expectedOk)
    {
        var height = 300;
        var placement = new DocumentImageFactory.Placement(70, 90, (int)Math.Round(ratio * height), height);
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Back, placement);

        var report = Inspect(picture, new ScriptedFaceDetector(), DocumentSide.Back);

        Assert.True(report.EdgesVisible);
        Assert.Equal(expectedOk, report.AspectOk);
        Assert.Equal(expectedOk, report.Codes.Count == 0);
    }

    [Fact]
    public void Inspect_ACardTurnedACounterClockwiseQuarter_IsReportedWithARotationOf90()
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var turned = DocumentImageFactory.TurnedCounterClockwise(upright);
        var detector = new PortraitStandInDetector();

        var report = Inspect(turned, detector);

        Assert.True(report.CardFound);
        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.Equal(90, report.RotationDegrees);
        Assert.Equal(6, report.ExifOrientation);
        Assert.True(report.PortraitInZone);
        Assert.Empty(report.Codes);
        AssertTurnMakesTheFaceUpright(turned, report);
    }

    [Fact]
    public void Inspect_ACardTurnedAClockwiseQuarter_IsReportedWithARotationOf270()
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var turned = DocumentImageFactory.TurnedClockwise(upright);

        var report = Inspect(turned, new PortraitStandInDetector());

        Assert.Equal(270, report.RotationDegrees);
        Assert.Equal(8, report.ExifOrientation);
        Assert.True(report.PortraitInZone);
        Assert.Empty(report.Codes);
        AssertTurnMakesTheFaceUpright(turned, report);
    }

    [Fact]
    public void Inspect_ACardUpsideDown_IsReportedWithARotationOf180()
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var turned = DocumentImageFactory.TurnedHalf(upright);

        var report = Inspect(turned, new PortraitStandInDetector());

        Assert.True(report.EdgesVisible);
        Assert.Equal(180, report.RotationDegrees);
        Assert.Equal(3, report.ExifOrientation);
        Assert.True(report.PortraitInZone);
        Assert.Empty(report.Codes);
        AssertTurnMakesTheFaceUpright(turned, report);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Inspect_TheBackOfACard_TakesItsTurnFromTheMrzBand(int degrees)
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Back, DocumentImageFactory.WholeCard);
        using var turned = Turn(upright, degrees);
        var detector = new ScriptedFaceDetector();

        var report = Inspect(turned, detector, DocumentSide.Back);

        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.Equal(degrees, report.RotationDegrees);
        Assert.False(report.PortraitInZone);
        Assert.Empty(report.Codes);
        using var made = turned.Oriented(report.ExifOrientation);
        var again = Inspect(made, new ScriptedFaceDetector(), DocumentSide.Back);
        Assert.Equal(0, again.RotationDegrees);
    }

    [Fact]
    public void Inspect_ATallCardWithNothingToTellItsTurn_CannotDecideItsOrientation()
    {
        // The front, with no face and no MRZ band on it: nothing says whether the top of the tall card is on the left or on the right.
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard, withFace: false);
        using var turned = DocumentImageFactory.TurnedCounterClockwise(upright);

        var report = Inspect(turned, new ScriptedFaceDetector());

        Assert.True(report.AspectOk);
        Assert.Equal(90, report.RotationDegrees);
        Assert.Equal([IdentityGuidanceCodes.DocumentOrientation, IdentityGuidanceCodes.DocumentPortraitMissing], report.Codes);
    }

    [Fact]
    public void Inspect_AFaceThatIsOnlyFoundSidewaysOnAnUprightCard_CannotDecideItsOrientation()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var detector = new ScriptedFaceDetector([], OnCard(InTheZone, 303, 480), [], []);

        var report = Inspect(picture, detector);

        Assert.True(report.AspectOk);
        Assert.Equal([IdentityGuidanceCodes.DocumentOrientation], report.Codes);
        Assert.Null(report.PortraitInZone);
    }

    // ---- AC3: the portrait where the format puts it ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Inspect_AFrontWithNoFaceInThePortraitRegion_IsPortraitMissing()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard, withFace: false);
        var detector = new ScriptedFaceDetector();

        var report = Inspect(picture, detector);

        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.False(report.PortraitInZone);
        Assert.Equal(0, report.RotationDegrees);
        Assert.Equal([IdentityGuidanceCodes.DocumentPortraitMissing], report.Codes);
        Assert.Equal(4, detector.Calls);
    }

    [Fact]
    public void Inspect_AFaceInThePortraitRegion_IsInTheZone()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var detector = FaceAt(InTheZone, 480, 303);

        var report = Inspect(picture, detector);

        Assert.True(report.PortraitInZone);
        Assert.Empty(report.Codes);
    }

    [Fact]
    public void Inspect_AFaceFarOutsideThePortraitRegion_IsALayoutMismatch()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var detector = FaceAt(new DocumentFormatRegion(0.7, 0.55, 0.15, 0.3), 480, 303);

        var report = Inspect(picture, detector);

        Assert.False(report.PortraitInZone);
        Assert.Equal([IdentityGuidanceCodes.DocumentLayoutMismatch], report.Codes);
    }

    [Fact]
    public void Inspect_AFaceThatTouchesThePortraitRegionButIsNotCentredInIt_IsPortraitMissing()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var detector = FaceAt(new DocumentFormatRegion(0.28, 0.3, 0.14, 0.3), 480, 303);

        var report = Inspect(picture, detector);

        Assert.False(report.PortraitInZone);
        Assert.Equal([IdentityGuidanceCodes.DocumentPortraitMissing], report.Codes);
    }

    [Fact]
    public void Inspect_AFrontSentAsTheBack_ShowsItsPortraitAndAsksNothingAboutIt()
    {
        // The side classifier tells a front from a back by the portrait: the face is looked for whatever side was declared, but only the side that has the portrait is told it is missing.
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);

        var report = Inspect(picture, new PortraitStandInDetector(), DocumentSide.Back);

        Assert.True(report.PortraitInZone);
        Assert.Empty(report.Codes);
    }

    /// <summary>
    /// A scripted detector that finds a face in the zone of the portrait only at the turn of the card it is asked about: 1 the card as it lies, 6 or 8 a quarter turn
    /// either way (the frame is then tall), 3 a half turn. <paramref name="cardWidth"/> and <paramref name="cardHeight"/> are those of the card made landscape.
    /// </summary>
    private static ScriptedFaceDetector FaceFoundAtTurn(int turn, int cardWidth, int cardHeight) => turn switch
    {
        1 => new ScriptedFaceDetector(OnCard(InTheZone, cardWidth, cardHeight), [], [], []),
        6 => new ScriptedFaceDetector([], OnCard(InTheZone, cardHeight, cardWidth), [], []),
        8 => new ScriptedFaceDetector([], [], OnCard(InTheZone, cardHeight, cardWidth), []),
        _ => new ScriptedFaceDetector([], [], [], OnCard(InTheZone, cardWidth, cardHeight)),
    };

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 6)]
    [InlineData(0, 8)]
    [InlineData(0, 3)]
    [InlineData(90, 1)]
    [InlineData(90, 6)]
    [InlineData(90, 8)]
    [InlineData(90, 3)]
    [InlineData(180, 1)]
    [InlineData(180, 6)]
    [InlineData(180, 8)]
    [InlineData(180, 3)]
    [InlineData(270, 1)]
    [InlineData(270, 6)]
    [InlineData(270, 8)]
    [InlineData(270, 3)]
    public void Inspect_ABackThatTheDetectorFindsAFaceOn_IsTurnedByItsMrzBandAndTheFaceOnlySaysWhereAPortraitIs(int degrees, int faceTurn)
    {
        // Measured before the band was put first: a face found sideways (6 or 8) on an upright back made it document_orientation, and one found at a half turn (3) turned
        // the back by 180 degrees; on a back upside down a face found upright (1) turned it back to 0. A face on the back is a front sent as the back or a stray detection.
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Back, DocumentImageFactory.WholeCard);
        using var turned = Turn(upright, degrees);

        var report = Inspect(turned, FaceFoundAtTurn(faceTurn, 480, 303), DocumentSide.Back);

        // A face found sideways is not judged (null); found upright or at a half turn, it is in the zone the scripted detector puts it in.
        bool? expectedPortrait = faceTurn is 6 or 8 ? null : true;
        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.Equal(degrees, report.RotationDegrees);
        Assert.Empty(report.Codes);
        Assert.Equal(expectedPortrait, report.PortraitInZone);
    }

    [Fact]
    public void Inspect_AFrontSentAsTheBackUpsideDown_StillShowsItsPortraitAndAsksNothingAboutIt()
    {
        // The band of a front means nothing, so the turn reported for it is not asserted: the picture is asked again as the wrong side. The portrait is what the classifier needs.
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var turned = DocumentImageFactory.TurnedHalf(upright);

        var report = Inspect(turned, new PortraitStandInDetector(), DocumentSide.Back);

        Assert.True(report.PortraitInZone);
        Assert.Empty(report.Codes);
    }

    [Fact]
    public void Inspect_TheFaceDetector_IsAskedWithTheScoreOfTheDocumentPhoto()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var detector = new RecordingDetector();
        var options = new BiometricModelOptions { DocumentDetectionScoreThreshold = 0.42 };

        Inspector(detector, options).Inspect(picture, Cie, DocumentSide.Front, CancellationToken.None);

        Assert.NotEmpty(detector.Scores);
        Assert.All(detector.Scores, score => Assert.Equal(0.42, score));
    }

    // ---- AC4: glare and blur are the statistics and the limits of the capture checks ----------------------------------------------------------------------------

    [Fact]
    public void Inspect_ASharpCardWithNoReflection_IsWithinTheLimitsOfTheCaptureChecks()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);

        var report = Inspect(picture, new PortraitStandInDetector());

        Assert.Equal(0, report.GlareShare);
        Assert.InRange(report.BlurRatio, 0.01, CaptureQualityRules.MaxBlurRatio);
        Assert.DoesNotContain(IdentityGuidanceCodes.DocumentGlare, report.Codes);
        Assert.DoesNotContain(IdentityGuidanceCodes.DocumentBlur, report.Codes);
    }

    [Fact]
    public void Inspect_AReflectionOverTheLimitOfTheCaptureChecks_IsDocumentGlare()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard, glareShare: 0.25);

        var report = Inspect(picture, new PortraitStandInDetector());

        Assert.True(report.CardFound);
        Assert.True(report.GlareShare > CaptureQualityRules.MaxDocumentGlareShare);
        Assert.Equal([IdentityGuidanceCodes.DocumentGlare], report.Codes);
    }

    [Fact]
    public void Inspect_AReflectionUnderTheLimitOfTheCaptureChecks_IsNotAskedAbout()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard, glareShare: 0.04);

        var report = Inspect(picture, new PortraitStandInDetector());

        Assert.InRange(report.GlareShare, 0.01, CaptureQualityRules.MaxDocumentGlareShare);
        Assert.Empty(report.Codes);
    }

    [Fact]
    public void Inspect_ABlurredPicture_IsDocumentBlur()
    {
        using var sharp = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var blurred = DocumentImageFactory.Blurred(sharp, radius: 4, passes: 3);

        var report = Inspect(blurred, new PortraitStandInDetector());

        Assert.True(report.BlurRatio > CaptureQualityRules.MaxBlurRatio);
        Assert.Contains(IdentityGuidanceCodes.DocumentBlur, report.Codes);
        Assert.Equal(IdentityGuidanceCodes.DocumentBlur, report.Codes[^1]);
    }

    // ---- a card that is not held straight (IDENT-TILT): the angle is looked for, the card is levelled -------------------------------------------------------------------
    // geometry-2026.11-1 read the card by the straight rows and columns of its edges: it tolerated a fifth of a degree either way and did not find the card from 0.35. The search of the
    // angle (geometry-2026.11-2) finds the card held up to ten degrees either way and hands on its levelled copy, and refuses the card held more crooked for it. The pictures are the card of
    // the factory (480 by 303 pixels in 640 by 480, its edges softened by the bilinear turn of the factory as in a photo), upright, upside down and turned a quarter. Real photos may not behave the same.

    private static IFaceDetector DetectorFor(DocumentSide side) => side == DocumentSide.Front ? new PortraitStandInDetector() : new ScriptedFaceDetector();

    [Theory]
    [InlineData(DocumentSide.Front, 0.2)]
    [InlineData(DocumentSide.Front, -0.2)]
    [InlineData(DocumentSide.Back, 0.2)]
    [InlineData(DocumentSide.Back, -0.2)]
    public void Inspect_ACardTurnedByTwoTenthsOfADegree_IsStillFoundWhole(DocumentSide side, double degrees)
    {
        using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
        using var turned = DocumentImageFactory.Rotated(upright, degrees);

        var report = Inspect(turned, DetectorFor(side), side);

        Assert.True(report.CardFound);
        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.Equal(0, report.RotationDegrees);
        Assert.Empty(report.Codes);
    }

    [Fact]
    public void Inspect_ACardThatLiesStraight_HasNoTiltAndNoLevelledCopy()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var copies = new List<Array>();

        DocumentGeometryReport report;
        DocumentTilt.Probe = copies;
        try
        {
            report = Inspect(picture, new PortraitStandInDetector());
        }
        finally
        {
            DocumentTilt.Probe = null;
        }

        using (report)
        {
            Assert.Equal(0, report.TiltDegrees);
            Assert.Null(report.RectifiedCard);
            Assert.Equal(DocumentGeometryRules.Version, report.RulesVersion);
            Assert.Empty(report.Codes);
        }

        // Nothing was levelled: the only buffers of the size of the picture are the two maps of its edges (no gray copy turned, no levelled card of 480 by 303 pixels).
        Assert.Equal(2, copies.OfType<byte[]>().Count(buffer => buffer.Length == DocumentImageFactory.Width * DocumentImageFactory.Height));
        Assert.DoesNotContain(copies.OfType<byte[]>(), buffer => buffer.Length == 480 * 303 * 3);
    }

    /// <summary>The cards of the tolerance: the front and the back, held 0.5 to 8 degrees either way.</summary>
    public static TheoryData<DocumentSide, double> CrookedCards()
    {
        var data = new TheoryData<DocumentSide, double>();
        foreach (var side in new[] { DocumentSide.Front, DocumentSide.Back })
        {
            foreach (var degrees in new[] { 0.5, 1, 2, 3, 5, 8 })
            {
                data.Add(side, degrees);
                data.Add(side, -degrees);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CrookedCards))]
    public void Inspect_ACardHeldCrooked_IsFoundWholeAndItsAngleIsMeasured(DocumentSide side, double degrees)
    {
        using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, degrees);

        using var report = Inspect(crooked, DetectorFor(side), side);

        Assert.True(report.CardFound);
        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.Equal(0, report.RotationDegrees);
        Assert.Empty(report.Codes);
        Assert.InRange(report.TiltDegrees, degrees - 0.5, degrees + 0.5);
        Assert.Equal(DocumentGeometryRules.Version, report.RulesVersion);
        Assert.InRange(report.AspectRatio, Cie.Layout.AspectRatio - Cie.Layout.AspectTolerance, Cie.Layout.AspectRatio + Cie.Layout.AspectTolerance);
        AssertTheCardLevelled(report.RectifiedCard, side);
    }

    [Theory]
    [MemberData(nameof(CrookedCards))]
    public void Inspect_ACardUpsideDownAndHeldCrooked_IsFoundWholeAndLevelled(DocumentSide side, double degrees)
    {
        // The same cards, upside down: the angle is the one the card has after the half turn that makes it upright, whatever the side it is held to.
        using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, degrees);
        using var upsideDown = DocumentImageFactory.TurnedHalf(crooked);

        using var report = Inspect(upsideDown, DetectorFor(side), side);

        Assert.True(report.CardFound);
        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.Equal(180, report.RotationDegrees);
        Assert.Empty(report.Codes);
        Assert.InRange(report.TiltDegrees, degrees - 0.5, degrees + 0.5);
        AssertTheCardLevelled(report.RectifiedCard, side);
    }

    /// <summary>The cards of the tolerance, turned a quarter either way and a half turn: the angle is the one of the card after the turn that makes it upright.</summary>
    public static TheoryData<DocumentSide, int, double> CrookedAndTurnedCards()
    {
        var data = new TheoryData<DocumentSide, int, double>();
        foreach (var side in new[] { DocumentSide.Front, DocumentSide.Back })
        {
            foreach (var turn in new[] { 90, 180, 270 })
            {
                foreach (var degrees in new[] { 3.0, -4.0, 8.0 })
                    data.Add(side, turn, degrees);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CrookedAndTurnedCards))]
    public void Inspect_ACardHeldCrookedAndTurned_IsLevelledAndTurnedUpright(DocumentSide side, int turn, double degrees)
    {
        using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, degrees);
        using var turned = Turn(crooked, turn);

        using var report = Inspect(turned, DetectorFor(side), side);

        Assert.True(report.EdgesVisible);
        Assert.True(report.AspectOk);
        Assert.Equal(turn, report.RotationDegrees);
        Assert.Empty(report.Codes);
        Assert.InRange(report.TiltDegrees, degrees - 0.5, degrees + 0.5);
        AssertTheCardLevelled(report.RectifiedCard, side);
    }

    [Theory]
    [InlineData(DocumentSide.Front, 12)]
    [InlineData(DocumentSide.Front, -12)]
    [InlineData(DocumentSide.Front, 15)]
    [InlineData(DocumentSide.Back, 12)]
    [InlineData(DocumentSide.Back, -15)]
    [InlineData(DocumentSide.Back, 20)]
    public void Inspect_ACardHeldMoreCrookedThanTheLimit_IsFoundAndRefusedForItsTurn(DocumentSide side, double degrees)
    {
        using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, degrees);
        var detector = new PortraitStandInDetector();

        using var report = Inspect(crooked, detector, side);

        Assert.True(report.CardFound);
        Assert.False(report.EdgesVisible);
        Assert.Equal([IdentityGuidanceCodes.DocumentEdgesNotVisible], report.Codes);
        Assert.InRange(report.TiltDegrees, degrees - 0.5, degrees + 0.5);
        Assert.True(Math.Abs(report.TiltDegrees) > DocumentGeometryRules.MaxTiltDegrees);
        Assert.Null(report.RectifiedCard);
        Assert.Null(report.PortraitInZone);
        Assert.Equal(0, detector.Calls);
    }

    [Theory]
    [InlineData(DocumentSide.Front, 25)]
    [InlineData(DocumentSide.Back, -30)]
    public void Inspect_ACardHeldBeyondTheSearch_IsNotFound(DocumentSide side, double degrees)
    {
        // The angle is looked for up to DocumentGeometryRules.TiltSearchDegrees either way: farther, nothing says that there is a card, as before.
        using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, degrees);

        using var report = Inspect(crooked, DetectorFor(side), side);

        Assert.False(report.CardFound);
        Assert.Equal([IdentityGuidanceCodes.DocumentNotFound], report.Codes);
        Assert.Equal(0, report.TiltDegrees);
        Assert.Null(report.RectifiedCard);
    }

    // ---- the pictures that are not a card stay refused, with the codes they had -------------------------------------------------------------------------------------------

    /// <summary>The pictures of point 4 of the brief: what the inspector answered before the search of the angle, and still answers.</summary>
    public static TheoryData<string> NegativePictures() => new()
    {
        "no card",
        "another ratio",
        "another ratio, turned",
        "a card too dim",
        "a card too dim, turned",
        "a page of text",
        "a page of text, turned",
        "cut by the frame",
        "too small",
        "too close to the border",
    };

    private static RasterImage NegativePicture(string name)
    {
        using var wrong = DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(100, 80, 400, 320), withFace: false);
        using var card = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var page = DocumentImageFactory.TextPage();
        using var turnedCard = DocumentImageFactory.Rotated(card, 3);
        using var turnedPage = DocumentImageFactory.Rotated(page, 4);
        return name switch
        {
            "no card" => DocumentImageFactory.Plain(120),
            "another ratio" => DocumentImageFactory.Brightened(wrong, 0),
            "another ratio, turned" => DocumentImageFactory.Rotated(wrong, 3),
            "a card too dim" => DocumentImageFactory.LowContrast(card, 40, 48),
            "a card too dim, turned" => DocumentImageFactory.LowContrast(turnedCard, 40, 48),
            "a page of text" => DocumentImageFactory.Brightened(page, 0),
            "a page of text, turned" => DocumentImageFactory.Brightened(turnedPage, 0),
            "cut by the frame" => DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(-100, 70, 480, 303)),
            "too small" => DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(200, 170, 240, 151)),
            _ => DocumentImageFactory.Render(Cie, DocumentSide.Front, new DocumentImageFactory.Placement(3, 70, 480, 303)),
        };
    }

    [Theory]
    [MemberData(nameof(NegativePictures))]
    public void Inspect_APictureThatIsNotAWholeCardOfTheFormat_IsRefusedWithTheCodeItHad(string name)
    {
        // The codes are the ones of geometry-2026.11-1, measured before the search of the angle was written. A card of another ratio that is held crooked is found at its angle (and says
        // what a card of another ratio says); a card held crooked and too dim, or a page of text, is not found, as before.
        string[] expected = name switch
        {
            "no card" => [IdentityGuidanceCodes.DocumentNotFound, IdentityGuidanceCodes.DocumentBlur],
            "another ratio" or "another ratio, turned" => [IdentityGuidanceCodes.DocumentLayoutMismatch],
            "cut by the frame" or "too small" or "too close to the border" => [IdentityGuidanceCodes.DocumentEdgesNotVisible],
            _ => [IdentityGuidanceCodes.DocumentNotFound],
        };
        using var picture = NegativePicture(name);
        var detector = new PortraitStandInDetector();

        using var report = Inspect(picture, detector);

        Assert.Equal(expected, report.Codes);
        Assert.False(report.EdgesVisible && report.AspectOk, name);
        Assert.Null(report.RectifiedCard);
        Assert.Null(report.PortraitInZone);
        Assert.Equal(0, detector.Calls);
    }

    // ---- the copies, the faults, the time ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Inspect_EveryCopyOfACardHeldCrooked_IsZeroed_AndTheLevelledCardWhenTheReportIsDisposed()
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, 4);
        var detector = new PortraitStandInDetector();
        var copies = new List<Array>();

        DocumentGeometryReport report;
        DocumentTilt.Probe = copies;
        try
        {
            report = Inspector(detector).Inspect(crooked, Cie, DocumentSide.Front, CancellationToken.None);
        }
        finally
        {
            DocumentTilt.Probe = null;
        }

        var levelled = Assert.IsType<RasterImage>(report.RectifiedCard);
        var held = levelled.Rgb;
        using (report)
        {
            // The picture was turned (a gray copy of its size), its edges were read twice (as it lies, then levelled), and the card was cut out of it levelled: every one of those buffers...
            Assert.True(copies.OfType<byte[]>().Count(buffer => buffer.Length == DocumentImageFactory.Width * DocumentImageFactory.Height) >= 5);
            Assert.Contains(copies, copy => ReferenceEquals(copy, held));

            // ...is zero now, but the levelled card, which is the report's and still holds the card.
            Assert.All(copies.Where(copy => !ReferenceEquals(copy, held)), AssertAllZero);
            Assert.Contains(held, value => value != 0);

            // The copies of the picture the face detector was shown are zero as well.
            Assert.NotEmpty(detector.SeenPixels);
            Assert.All(detector.SeenPixels, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));
        }

        // The owner of the report zeroes the levelled card, once or twice.
        Assert.All(held, value => Assert.Equal(0, value));
        Assert.Empty(levelled.Rgb);
        report.Dispose();
    }

    [Fact]
    public void Inspect_EveryCopyOfACardHeldCrookedAndUpsideDown_IsZeroed_AlsoTheTurnedCopyOfTheReport()
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, -6);
        using var turned = DocumentImageFactory.TurnedHalf(crooked);
        var copies = new List<Array>();

        DocumentGeometryReport report;
        DocumentTilt.Probe = copies;
        try
        {
            report = Inspect(turned, new PortraitStandInDetector());
        }
        finally
        {
            DocumentTilt.Probe = null;
        }

        var held = Assert.IsType<RasterImage>(report.RectifiedCard).Rgb;
        using (report)
        {
            Assert.Equal(180, report.RotationDegrees);
            Assert.Contains(held, value => value != 0);

            // The card levelled and not yet turned was a copy of its own and is zeroed; the turned one is the report's.
            Assert.DoesNotContain(copies, copy => ReferenceEquals(copy, held));
            Assert.All(copies, AssertAllZero);
        }

        Assert.All(held, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Inspect_TheCopiesOfACardHeldCrooked_AreZeroedWhenTheDetectorFails()
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, 5);
        var detector = new ScriptedFaceDetector { Failure = new BiometricUnavailableException() };
        var copies = new List<Array>();

        DocumentTilt.Probe = copies;
        try
        {
            Assert.Throws<BiometricUnavailableException>(() => Inspect(crooked, detector));
        }
        finally
        {
            DocumentTilt.Probe = null;
        }

        // The card was found and levelled before the face was asked for, so the levelled card was made, and no report was there to hold it.
        Assert.Contains(copies.OfType<byte[]>(), buffer => buffer.Length > 400_000 && buffer.Length < 500_000);
        Assert.NotEmpty(detector.SeenPixels);
        Assert.All(copies, AssertAllZero);
        Assert.All(detector.SeenPixels, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));
    }

    [Fact]
    public void Inspect_ACardHeldCrookedWhoseRequestIsCancelled_StopsAndZeroesWhatItMade()
    {
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, 3);
        using var source = new CancellationTokenSource();
        var detector = new CancelOnFirstQuestion(source);
        var copies = new List<Array>();

        DocumentTilt.Probe = copies;
        try
        {
            // The card is found at its angle and levelled; the request is cancelled when the face is asked for, so there is no report to hold the levelled card.
            Assert.Throws<OperationCanceledException>(() => Inspector(detector).Inspect(crooked, Cie, DocumentSide.Front, source.Token));
        }
        finally
        {
            DocumentTilt.Probe = null;
        }

        Assert.Equal(1, detector.Calls);
        Assert.Contains(copies.OfType<byte[]>(), buffer => buffer.Length == 480 * 303 * 3);
        Assert.All(copies, AssertAllZero);
        Assert.All(detector.SeenPixels, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));
    }

    [Fact]
    public void Inspect_ACardHeldCrooked_CostsAFewTimesTheStraightCard()
    {
        using var straight = DocumentImageFactory.Render(Cie, DocumentSide.Back, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(straight, 3);
        var inspector = Inspector(new ScriptedFaceDetector());
        inspector.Inspect(straight, Cie, DocumentSide.Back, CancellationToken.None).Dispose();
        inspector.Inspect(crooked, Cie, DocumentSide.Back, CancellationToken.None).Dispose();

        // The best of a few rounds: the machines of the build are loaded, and what is measured is the work, not the queue for the processor.
        double Best(RasterImage picture)
        {
            var best = double.MaxValue;
            for (var round = 0; round < 3; round++)
            {
                var watch = Stopwatch.StartNew();
                for (var run = 0; run < 5; run++)
                    inspector.Inspect(picture, Cie, DocumentSide.Back, CancellationToken.None).Dispose();

                best = Math.Min(best, watch.Elapsed.TotalMilliseconds / 5);
            }

            return best;
        }

        var straightMs = Best(straight);
        var crookedMs = Best(crooked);

        // Measured in the pull request (about a hundred milliseconds is the aim on one core); the assertion only guards against a search that no longer scales.
        Assert.True(crookedMs < 10 * straightMs + 50, $"{crookedMs:F0} ms for the card held crooked against {straightMs:F0} ms for the card straight");
        Assert.True(crookedMs < 1000, $"{crookedMs:F0} ms per inspection");
    }

    /// <summary>The card levelled is the canonical card: landscape, the size of the card, its portrait (the front) or its MRZ band (the back) where the layout puts it.</summary>
    private static void AssertTheCardLevelled(RasterImage? card, DocumentSide side)
    {
        Assert.NotNull(card);
        Assert.InRange(card.Width, 470, 495);
        Assert.InRange(card.Height, 295, 315);
        var layout = Cie.Layout.Sides[DocumentSides.CodeOf(side)];
        if (layout.Portrait is { } zone)
        {
            // The middle of the zone of the portrait below the dark bar of the stand-in is skin, and the bar is where it is drawn, when the card lies upright and level.
            Assert.True(DocumentImageFactory.IsSkin(card.Rgb, PixelAt(card, zone.X + (zone.W * 0.5), zone.Y + (zone.H * 0.7))), "the portrait is where the layout puts it");
            Assert.True(DocumentImageFactory.IsHair(card.Rgb, PixelAt(card, zone.X + (zone.W * 0.5), zone.Y + (zone.H * 0.24))), "the bar of the portrait is where the layout puts it");
        }

        if (layout.MrzRegion is { } band)
        {
            // The band is the densest text of the card: much more ink than the region opposite to it.
            var opposite = new DocumentFormatRegion(1 - band.X - band.W, 1 - band.Y - band.H, band.W, band.H);
            Assert.True(InkShare(card, band) > 1.5 * InkShare(card, opposite), "the MRZ band is where the layout puts it");
        }
    }

    private static int PixelAt(RasterImage card, double shareX, double shareY) =>
        ((((int)(shareY * card.Height)) * card.Width) + (int)(shareX * card.Width)) * 3;

    /// <summary>The share of the pixels of the region of the card that are dark (ink).</summary>
    internal static double InkShare(RasterImage card, DocumentFormatRegion region)
    {
        var left = (int)(region.X * card.Width);
        var top = (int)(region.Y * card.Height);
        var right = Math.Min(card.Width, (int)((region.X + region.W) * card.Width));
        var bottom = Math.Min(card.Height, (int)((region.Y + region.H) * card.Height));
        var ink = 0;
        var all = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var at = ((y * card.Width) + x) * 3;
                all++;
                if (card.Rgb[at] + card.Rgb[at + 1] + card.Rgb[at + 2] < 300)
                    ink++;
            }
        }

        return all == 0 ? 0 : (double)ink / all;
    }

    private static void AssertAllZero(Array buffer)
    {
        foreach (var value in buffer)
        {
            if (Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) != 0)
                Assert.Fail($"A buffer of {buffer.Length} {buffer.GetType().GetElementType()?.Name} made of the picture was not zeroed.");
        }
    }

    /// <summary>A face detector whose first question cancels the request (the caller gave up while the picture was being read), and which keeps the pictures it was shown.</summary>
    private sealed class CancelOnFirstQuestion(CancellationTokenSource source) : IFaceDetector
    {
        public int Calls { get; private set; }

        public List<byte[]> SeenPixels { get; } = [];

        public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
        {
            Calls++;
            SeenPixels.Add(image.Rgb);
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }
    }

    // ---- the picture, the copies, the faults ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Inspect_APictureLargerThanTheWorkingSize_GivesTheSameCard()
    {
        using var small = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var big = new DocumentImageFactory.Placement(160, 140, 960, 606);
        using var large = DocumentImageFactory.Render(Cie, DocumentSide.Front, big, imageWidth: 1280, imageHeight: 960);

        var reportOfSmall = Inspect(small, new PortraitStandInDetector());
        var reportOfLarge = Inspect(large, new PortraitStandInDetector());

        Assert.True(reportOfLarge.EdgesVisible);
        Assert.True(reportOfLarge.AspectOk);
        Assert.True(reportOfLarge.PortraitInZone);
        AssertNear(reportOfSmall.CardBox!.X, reportOfLarge.CardBox!.X);
        AssertNear(reportOfSmall.CardBox.Y, reportOfLarge.CardBox.Y);
        AssertNear(reportOfSmall.CardBox.W, reportOfLarge.CardBox.W);
        AssertNear(reportOfSmall.CardBox.H, reportOfLarge.CardBox.H);
    }

    [Fact]
    public void Inspect_TheCopiesItMakes_AreZeroedAndThePictureIsLeftAsItWas()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var before = (byte[])picture.Rgb.Clone();
        var detector = new ScriptedFaceDetector();

        Inspect(picture, detector);

        Assert.Equal(4, detector.SeenPixels.Count);
        Assert.All(detector.SeenPixels, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));
        Assert.Equal(before, picture.Rgb);
    }

    [Fact]
    public void Inspect_TheCopiesItMakes_AreZeroedWhenTheDetectorFails()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var detector = new ScriptedFaceDetector { Failure = new BiometricUnavailableException() };

        Assert.Throws<BiometricUnavailableException>(() => Inspect(picture, detector));

        Assert.NotEmpty(detector.SeenPixels);
        Assert.All(detector.SeenPixels, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));
    }

    [Fact]
    public void Inspect_ACancelledRequest_StopsWithoutAReport()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => Inspect(picture, new ScriptedFaceDetector(), DocumentSide.Front, source.Token));
    }

    [Fact]
    public void Inspect_AFormatWithNoPortraitZone_NeverAsksTheFaceDetector()
    {
        var withoutPortrait = Cie with
        {
            Layout = Cie.Layout with
            {
                Sides = Cie.Layout.Sides.ToDictionary(
                    side => side.Key, side => side.Value with { Portrait = null }),
            },
            RequiredChecks = [DocumentFormatChecks.Layout],
        };
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard, withFace: false);
        var detector = new ScriptedFaceDetector();

        var report = Inspector(detector).Inspect(picture, withoutPortrait, DocumentSide.Front, CancellationToken.None);

        Assert.True(report.AspectOk);
        Assert.Null(report.PortraitInZone);
        Assert.Equal(0, detector.Calls);
        Assert.Empty(report.Codes);
    }

    [Fact]
    public void AddCasazenServices_RegistersTheInspectorAsASingleton_AndItResolvesWithTheFaceDetectorOfTheApplication()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddJsonFile(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false).Build());
        services.AddLogging();
        services.AddCasazenServices();
        using var provider = services.BuildServiceProvider();

        var inspector = provider.GetRequiredService<DocumentGeometryInspector>();

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DocumentGeometryInspector)).Lifetime);
        Assert.Same(inspector, provider.GetRequiredService<DocumentGeometryInspector>());
    }

    [Fact]
    public void Inspect_OneInspectionOfAPictureOf640Pixels_TakesAFractionOfASecond()
    {
        using var picture = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        var inspector = Inspector(new ScriptedFaceDetector());
        inspector.Inspect(picture, Cie, DocumentSide.Back, CancellationToken.None);

        var watch = Stopwatch.StartNew();
        const int runs = 20;
        for (var run = 0; run < runs; run++)
            inspector.Inspect(picture, Cie, DocumentSide.Back, CancellationToken.None);

        watch.Stop();

        // The measured value is in the pull request (about 100 ms is the aim on one core); the assertion only guards against a method that no longer scales.
        Assert.True(watch.Elapsed.TotalMilliseconds / runs < 500, $"{watch.Elapsed.TotalMilliseconds / runs:F0} ms per inspection");
    }

    // ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static RasterImage Turn(RasterImage source, int degrees) => degrees switch
    {
        90 => DocumentImageFactory.TurnedCounterClockwise(source),
        180 => DocumentImageFactory.TurnedHalf(source),
        270 => DocumentImageFactory.TurnedClockwise(source),
        _ => DocumentImageFactory.Brightened(source, 0),
    };

    /// <summary>The box is the card to within two pixels of the picture: the edge of a card is a pixel or two wide.</summary>
    private static void AssertNear(double expected, double actual) => Assert.InRange(actual, expected - 0.008, expected + 0.008);

    /// <summary>The turn the report asks for makes the picture upright: the detector that only finds an upright face finds it there.</summary>
    private static void AssertTurnMakesTheFaceUpright(RasterImage turned, DocumentGeometryReport report)
    {
        using var made = turned.Oriented(report.ExifOrientation);
        var found = new PortraitStandInDetector().Detect(made, 0.6, CancellationToken.None);
        Assert.Single(found);
    }

    /// <summary>A detector that finds nothing and keeps the scores it was asked with.</summary>
    private sealed class RecordingDetector : IFaceDetector
    {
        public List<double> Scores { get; } = [];

        public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
        {
            Scores.Add(minScore);
            return [];
        }
    }
}
