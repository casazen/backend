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
/// loads it from its file.
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

    // ---- a card that is not held straight: the tolerance, as measured -------------------------------------------------------------------------------------------------
    // The detector reads the card by the straight rows and columns of its edges (no search of the angle in this slice), so it tolerates very little. These tests hold what the
    // code really does on the generated card (480 by 303 pixels in 640 by 480, edges softened by the bilinear turn of the factory), the figures of the documentation of the
    // inspector and of the pull request: found whole up to 0.2 degrees either way, not found from 0.35 degrees (0.25 on most of the pictures). Real photos may not behave the same.

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

    [Theory]
    [InlineData(DocumentSide.Front, 1)]
    [InlineData(DocumentSide.Front, -1)]
    [InlineData(DocumentSide.Front, 3)]
    [InlineData(DocumentSide.Front, -3)]
    [InlineData(DocumentSide.Back, 1)]
    [InlineData(DocumentSide.Back, -1)]
    [InlineData(DocumentSide.Back, 3)]
    [InlineData(DocumentSide.Back, -3)]
    public void Inspect_ACardTurnedByADegreeOrMore_IsNotFound(DocumentSide side, double degrees)
    {
        using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
        using var turned = DocumentImageFactory.Rotated(upright, degrees);
        var detector = new ScriptedFaceDetector();

        var report = Inspect(turned, detector, side);

        Assert.False(report.CardFound);
        Assert.Null(report.CardBox);
        Assert.False(report.EdgesVisible);
        Assert.False(report.AspectOk);
        Assert.Equal([IdentityGuidanceCodes.DocumentNotFound], report.Codes);
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public void Inspect_ACardTurnedMoreThanTheTolerance_IsNeverTakenForAWholeCard()
    {
        // From 0.25 degrees, either way, up to 10: whatever the turn, the guest is asked for a new capture of a card that is found or not, never given a whole card of its layout.
        foreach (var side in new[] { DocumentSide.Front, DocumentSide.Back })
        {
            foreach (var degrees in new[] { 0.25, 0.3, 0.35, 0.5, 1, 1.5, 2, 3, 5, 10 })
            {
                foreach (var sign in new[] { 1, -1 })
                {
                    using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
                    using var turned = DocumentImageFactory.Rotated(upright, sign * degrees);

                    var report = Inspect(turned, DetectorFor(side), side);

                    var turn = $"{side} turned by {sign * degrees} degrees";
                    Assert.False(report.EdgesVisible, turn);
                    Assert.Contains(report.Codes, code => code is IdentityGuidanceCodes.DocumentNotFound or IdentityGuidanceCodes.DocumentEdgesNotVisible);
                }
            }
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
