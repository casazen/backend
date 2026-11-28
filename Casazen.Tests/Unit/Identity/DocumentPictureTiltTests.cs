using System.Reflection;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public class DocumentPictureTiltTests
{
    private static readonly DocumentFormat Cie = DocumentStepSelfHostedTests.Cie;

    private static DocumentPictureAnalyzer Analyzer(IZonedFieldReader zoned) =>
        new(new DocumentGeometryInspector(new PortraitStandInDetector(), Options.Create(new BiometricModelOptions())), zoned, Options.Create(new BiometricModelOptions()));

    /// <summary>A photo of a side of the card held <paramref name="degrees"/> clockwise, as the upload of a guest (a PNG): the front unless <paramref name="side"/> says the back.</summary>
    private static byte[] Photo(double degrees, DocumentSide side = DocumentSide.Front)
    {
        using var upright = DocumentImageFactory.Render(Cie, side, DocumentImageFactory.WholeCard);
        using var held = degrees == 0 ? DocumentImageFactory.Brightened(upright, 0) : DocumentImageFactory.Rotated(upright, degrees);
        return TestImages.Png(held.Width, held.Height, held.Rgb);
    }

    [Fact]
    public async Task ReadFields_ACardHeldCrooked_HandsTheReaderTheLevelledCardOfTheReport_ItselfAndNotACopyOfThePicture()
    {
        var zoned = new RecordingZonedReader();
        using var picture = Analyzer(zoned).Open(Photo(5));
        var report = picture.Inspect(Cie, DocumentSide.Front, CancellationToken.None);

        await picture.ReadFieldsAsync(Cie, DocumentSide.Front, report, CancellationToken.None);

        Assert.Equal(1, zoned.Calls);
        Assert.NotNull(report.RectifiedCard);
        Assert.Same(report.RectifiedCard, zoned.Card);
        Assert.InRange(report.TiltDegrees, 4.5, 5.5);
        Assert.NotEmpty(report.RectifiedCard.Rgb);
        Assert.InRange(report.RectifiedCard.Width, 470, 495);
        Assert.InRange(report.RectifiedCard.Height, 295, 315);
    }

    [Fact]
    public async Task ReadFields_TheMrzReadingOfACardHeldCrooked_IsHandedTheLevelledCardUpright()
    {
        var mrz = new ScriptedMrzReader();
        var zoned = new ZonedFieldReader(
            ZoneScriptedPageReader.Answering(new Dictionary<byte, string>()), mrz, NullLogger<ZonedFieldReader>.Instance, Options.Create(new MrzOcrOptions()));
        using var picture = Analyzer(zoned).Open(Photo(-5, DocumentSide.Back));
        var report = picture.Inspect(Cie, DocumentSide.Back, CancellationToken.None);

        await picture.ReadFieldsAsync(Cie, DocumentSide.Back, report, CancellationToken.None);

        Assert.True(DocumentPicture.UsesRectifiedCard(report));
        Assert.Equal(1, mrz.Calls);
        using var handed = RasterImage.Decode(Assert.IsType<byte[]>(mrz.Picture), 25_000_000);
        Assert.InRange(handed.Width, 470, 495);
        Assert.InRange(handed.Height, 295, 315);
        var band = Assert.IsType<DocumentFormatRegion>(Cie.Layout.Sides["back"].MrzRegion);
        var opposite = new DocumentFormatRegion(1 - band.X - band.W, 1 - band.Y - band.H, band.W, band.H);
        Assert.True(DocumentGeometryInspectorTests.InkShare(handed, band) > 1.5 * DocumentGeometryInspectorTests.InkShare(handed, opposite));
    }

    [Fact]
    public async Task ReadFields_ACardThatLiesStraight_IsCutOutAsItLies_WithNoLevelledCopy()
    {
        var zoned = new RecordingZonedReader();
        using var picture = Analyzer(zoned).Open(Photo(0));
        var report = picture.Inspect(Cie, DocumentSide.Front, CancellationToken.None);

        await picture.ReadFieldsAsync(Cie, DocumentSide.Front, report, CancellationToken.None);

        Assert.Null(report.RectifiedCard);
        Assert.Equal(0, report.TiltDegrees);
        Assert.Equal(1, zoned.Calls);
        Assert.NotNull(zoned.Card);
        Assert.InRange(zoned.Card.Width, 470, 495);
        Assert.InRange(zoned.Card.Height, 295, 315);
    }

    [Fact]
    public async Task ReadFields_ACardHeldMoreCrookedThanTheLimit_IsNotRead()
    {
        var zoned = new RecordingZonedReader();
        using var picture = Analyzer(zoned).Open(Photo(13));
        var report = picture.Inspect(Cie, DocumentSide.Front, CancellationToken.None);

        var fields = await picture.ReadFieldsAsync(Cie, DocumentSide.Front, report, CancellationToken.None);

        Assert.True(report.CardFound);
        Assert.False(report.EdgesVisible);
        Assert.False(DocumentPicture.IsMeasurable(report));
        Assert.Empty(fields);
        Assert.Equal(0, zoned.Calls);
    }

    [Fact]
    public void Dispose_ZeroesTheLevelledCardOfEveryReportOfThePicture()
    {
        var zoned = new RecordingZonedReader();
        var picture = Analyzer(zoned).Open(Photo(-4));
        var first = picture.Inspect(Cie, DocumentSide.Front, CancellationToken.None);
        var second = picture.Inspect(Cie, DocumentSide.Front, lookForPortrait: false, CancellationToken.None);
        var firstCard = Assert.IsType<RasterImage>(first.RectifiedCard);
        var secondCard = Assert.IsType<RasterImage>(second.RectifiedCard);
        var pixelsOfFirst = firstCard.Rgb;
        var pixelsOfSecond = secondCard.Rgb;
        Assert.NotSame(pixelsOfFirst, pixelsOfSecond);
        Assert.Contains(pixelsOfFirst, value => value != 0);
        Assert.Contains(pixelsOfSecond, value => value != 0);

        picture.Dispose();

        Assert.All(pixelsOfFirst, value => Assert.Equal(0, value));
        Assert.All(pixelsOfSecond, value => Assert.Equal(0, value));
        Assert.Empty(firstCard.Rgb);
        Assert.Empty(secondCard.Rgb);
        picture.Dispose();
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.2, false)]
    [InlineData(-0.25, false)]
    [InlineData(0.3, true)]
    [InlineData(-3.0, true)]
    [InlineData(9.5, true)]
    public void UsesRectifiedCard_IsTheCardTurnedByMoreThanTheRoundingStep(double degrees, bool expected)
    {
        using var measurable = new DocumentGeometryReport(
            true, new NormalizedBox(0.1, 0.1, 0.8, 0.8), true, 1.586, true, 0, true, 0, 0.5, [], DocumentGeometryRules.Version, degrees, new RasterImage(2, 2, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
        using var notVisible = measurable with { EdgesVisible = false };

        Assert.Equal(expected, DocumentPicture.UsesRectifiedCard(measurable));
        Assert.False(DocumentPicture.UsesRectifiedCard(notVisible));
        Assert.False(DocumentPicture.UsesRectifiedCard(measurable with { RectifiedCard = null }));
        Assert.False(DocumentPicture.UsesRectifiedCard(measurable with { Codes = [IdentityGuidanceCodes.DocumentOrientation] }));
    }

    [Fact]
    public void TheInspectorAndItsHelpers_HaveNoLogger_SoTheyCanLogNothingOfAPicture()
    {
        var constructor = Assert.Single(typeof(DocumentGeometryInspector).GetConstructors());
        Assert.Equal([typeof(IFaceDetector), typeof(IOptions<BiometricModelOptions>)], constructor.GetParameters().Select(parameter => parameter.ParameterType));

        var types = new[] { typeof(DocumentGeometryInspector), typeof(DocumentEdgeMaps), typeof(DocumentEdgePoints), typeof(DocumentTilt), typeof(DocumentGeometryReport) };
        foreach (var type in types)
        {
            var members = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(field => field.FieldType)
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SelectMany(ctor => ctor.GetParameters().Select(parameter => parameter.ParameterType)));
            Assert.DoesNotContain(members, member => typeof(ILogger).IsAssignableFrom(member) || typeof(ILoggerFactory).IsAssignableFrom(member));
        }
    }

    [Fact]
    public void AReportThatCarriesALevelledCard_PrintsNoPixel()
    {
        using var report = new DocumentGeometryReport(
            true, new NormalizedBox(0.1, 0.1, 0.8, 0.8), true, 1.586, true, 0, true, 0, 0.5, [], DocumentGeometryRules.Version, 4, new RasterImage(2, 2, [171, 172, 173, 174, 175, 176, 177, 178, 179, 180, 181, 182]));

        var printed = report.ToString();

        Assert.Contains(nameof(DocumentGeometryReport), printed, StringComparison.Ordinal);
        foreach (var pixel in new[] { "171", "172", "173", "174", "175", "176", "177", "178", "179", "180", "181", "182" })
            Assert.DoesNotContain(pixel, printed, StringComparison.Ordinal);
    }

    private sealed class RecordingZonedReader : IZonedFieldReader
    {
        public int Calls { get; private set; }

        public RasterImage? Card { get; private set; }

        public Task<IReadOnlyList<ZonedField>> ReadAsync(RasterImage canonicalCard, DocumentFormat format, DocumentSide side, CancellationToken cancellationToken = default)
        {
            Calls++;
            Card = canonicalCard;
            return Task.FromResult<IReadOnlyList<ZonedField>>([]);
        }
    }
}
