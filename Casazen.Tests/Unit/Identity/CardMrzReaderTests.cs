using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// The pictures are built in code: the back of the Italian card on a dark ground, its MRZ drawn as thin strokes the way printed text is, read by the real <see cref="MrzExtractionService"/> and the real
/// <see cref="MrzImageLocator"/>. Only the OCR is a double (<see cref="StrokeCountLineReader"/>): what is proved is where the MRZ is looked for, not the weights of the OCR.
/// </summary>
public sealed class CardMrzReaderTests
{
    private const byte Ground = 70;
    private const byte Paper = 235;
    private const byte Ink = 25;
    private const int PrintWidth = 2;
    private const int PrintPitch = 12;
    private const int StrokeWidth = 3;
    private const int StrokePitch = 14;
    private const int LineHeight = 22;
    private const int MrzStrokes = 29;
    private const int MinStrokeWidth = 3;
    private const int WorkingSide = 2000;

    private static readonly DocumentFormat Cie = DocumentStepSelfHostedTests.Cie;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));
    private static readonly int[] LineTops = [196, 228, 260];

    private static readonly string[] Forbidden =
    [
        MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3, "ERIKSSON", "ANNA", "MARIA", "D23145890",
        "740812", "120415", "1974-08-12", "12/08/1974", "08/12/1974", "12.08.1974", "2012-04-15", "15/04/2012", "15.04.2012", "<<<",
    ];

    private static DocumentPictureAnalyzer Analyzer() =>
        new(
            new DocumentGeometryInspector(new PortraitStandInDetector(), Options.Create(new BiometricModelOptions())),
            new DocumentStepSelfHostedTests.ScriptedZonedReader(),
            Options.Create(new BiometricModelOptions()));

    private static (MrzExtractionService Service, CountingLocator Locator, CapturingLogger<MrzExtractionService> Log) Reader(params string[] mrzLines)
    {
        var locator = new CountingLocator(new MrzImageLocator(Options.Create(new MrzOcrOptions())));
        var log = new CapturingLogger<MrzExtractionService>();
        var service = new MrzExtractionService(log, locator, new StrokeCountLineReader(mrzLines), Options.Create(new MrzOcrOptions()), Clock);
        return (service, locator, log);
    }

    private static (MrzExtractionService Service, CountingLocator Locator, CapturingLogger<MrzExtractionService> Log) SpecimenReader() =>
        Reader(MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3);

    private static byte[] Png(RasterImage picture) => TestImages.Png(picture.Width, picture.Height, picture.Rgb);

    // The lines of the MRZ have 29, 30 and 31 strokes, which is what the OCR double tells them by, and they are the densest print of the card, as the locator expects.
    private static RasterImage BackOnDarkGround()
    {
        var card = DocumentImageFactory.WholeCard;
        var rgb = new byte[DocumentImageFactory.Width * DocumentImageFactory.Height * 3];
        Array.Fill(rgb, Ground);
        Fill(rgb, card.Left, card.Top, card.Width, card.Height, Paper);
        foreach (var field in Cie.Layout.Sides[DocumentSides.Back].Fields)
        {
            var left = card.Left + (int)Math.Round(field.Zone.X * card.Width);
            var right = left + (int)Math.Round(field.Zone.W * card.Width);
            var top = card.Top + (int)Math.Round((field.Zone.Y + (field.Zone.H * 0.3)) * card.Height);
            var height = Math.Max(3, (int)Math.Round(field.Zone.H * 0.4 * card.Height));
            for (var x = left; x + PrintWidth <= right; x += PrintPitch)
                Fill(rgb, x, top, PrintWidth, height, Ink);
        }

        for (var line = 0; line < LineTops.Length; line++)
        {
            for (var stroke = 0; stroke < MrzStrokes + line; stroke++)
                Fill(rgb, card.Left + 12 + (stroke * StrokePitch), card.Top + LineTops[line], StrokeWidth, LineHeight, Ink);
        }

        return new RasterImage(DocumentImageFactory.Width, DocumentImageFactory.Height, rgb);
    }

    private static void Fill(byte[] rgb, int left, int top, int width, int height, byte level)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
                rgb.AsSpan(((y * DocumentImageFactory.Width) + x) * 3, 3).Fill(level);
        }
    }

    [Fact]
    public async Task ReadAsync_TheBackOfACardOnADarkGround_IsReadExactlyOnTheCard_WhereTheWholePhotoGivesNothing()
    {
        using var drawn = BackOnDarkGround();
        var photo = Png(drawn);
        var (mrz, _, _) = SpecimenReader();
        using var picture = Analyzer().Open(photo);
        var report = picture.Inspect(Cie, DocumentSide.Back, Ct);
        Assert.True(DocumentPicture.IsMeasurable(report));

        using (var whole = new MemoryStream(photo, writable: false))
            Assert.Null((await mrz.ReadAsync(whole, Ct)).Result);

        var reading = await CardMrzReader.ReadAsync(mrz, picture, report, photo, WorkingSide, Ct);

        var read = Assert.IsType<MrzExtractionResult>(reading.Result);
        Assert.True(read.CheckDigitsValid);
        Assert.Equal("D23145890", read.DocumentNumber);
        Assert.Equal(new DateTime(1974, 8, 12, 0, 0, 0, DateTimeKind.Utc), read.DateOfBirth);
        Assert.Equal(new DateTime(2012, 4, 15, 0, 0, 0, DateTimeKind.Utc), read.ExpirationDate);
        Assert.Equal("ERIKSSON", read.Surname);
        Assert.Equal("ANNA MARIA", read.GivenNames);
        Assert.Equal("IdCard", read.DocumentType);
        Assert.Empty(reading.Quality);
    }

    [Fact]
    public async Task ReadAsync_TheCardOfTheGeometry_IsReadInTheTurnItLies_WithoutTheFourTurnSpinOfTheWholePhoto()
    {
        using var drawn = BackOnDarkGround();
        var photo = Png(drawn);
        var onCard = SpecimenReader();
        using var picture = Analyzer().Open(photo);
        var report = picture.Inspect(Cie, DocumentSide.Back, Ct);

        var reading = await CardMrzReader.ReadAsync(onCard.Service, picture, report, photo, WorkingSide, Ct);

        Assert.True(reading.Result is { CheckDigitsValid: true });
        var page = Assert.Single(onCard.Locator.Pages);
        Assert.InRange(page.Width, 470, 495);
        Assert.InRange(page.Height, 295, 315);

        var wholePhoto = SpecimenReader();
        using (var stream = new MemoryStream(photo, writable: false))
            Assert.Null((await wholePhoto.Service.ReadAsync(stream, Ct)).Result);

        Assert.Equal(4, wholePhoto.Locator.Calls);
    }

    [Fact]
    public async Task ReadCardAsync_ACardThatDoesNotLieUpright_IsStillRead_ButOnlyAfterTheTurnItLiesIsTried()
    {
        using var drawn = BackOnDarkGround();
        var place = DocumentImageFactory.WholeCard;
        using var card = drawn.Cropped(place.Left, place.Top, place.Width, place.Height);
        using var upsideDown = DocumentImageFactory.TurnedHalf(card);
        var reader = SpecimenReader();

        var reading = await CardMrzReader.ReadCardAsync(reader.Service, upsideDown, WorkingSide, Ct);

        Assert.True(reading.Result is { CheckDigitsValid: true });
        Assert.Equal(4, reader.Locator.Calls);
    }

    [Fact]
    public async Task ReadUprightAsync_ABlockThatFailsItsDigits_IsTheAnswer_WhereReadAsyncLooksForItInTheOtherTurnsToo()
    {
        using var drawn = BackOnDarkGround();
        var place = DocumentImageFactory.WholeCard;
        using var card = drawn.Cropped(place.Left, place.Top, place.Width, place.Height);
        var png = CardPngEncoder.Encode(card);
        var altered = MrzSpecimens.Td1Line2[..2] + "9" + MrzSpecimens.Td1Line2[3..];
        var upright = Reader(MrzSpecimens.Td1Line1, altered, MrzSpecimens.Td1Line3);
        var anyTurn = Reader(MrzSpecimens.Td1Line1, altered, MrzSpecimens.Td1Line3);

        using (var stream = new MemoryStream(png, writable: false))
            Assert.False((await upright.Service.ReadUprightAsync(stream, Ct)).Result!.CheckDigitsValid);

        using (var stream = new MemoryStream(png, writable: false))
            Assert.False((await anyTurn.Service.ReadAsync(stream, Ct)).Result!.CheckDigitsValid);

        Assert.Equal(1, upright.Locator.Calls);
        Assert.Equal(4, anyTurn.Locator.Calls);
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("cut-by-the-frame")]
    public async Task ReadAsync_AGeometryWithNoCardToStraighten_ReadsTheWholePhotoAsItLies(string picture)
    {
        using var drawn = picture == "nothing"
            ? DocumentImageFactory.Plain(120)
            : DocumentImageFactory.Render(Cie, DocumentSide.Back, new DocumentImageFactory.Placement(-100, 70, 480, 303));
        var photo = Png(drawn);
        var mrz = new ScriptedMrzReader(new MrzReading(null, [MrzQualityCodes.MrzNotFound]));
        using var opened = Analyzer().Open(photo);
        var report = opened.Inspect(Cie, DocumentSide.Back, Ct);
        Assert.False(DocumentPicture.IsMeasurable(report));

        var reading = await CardMrzReader.ReadAsync(mrz, opened, report, photo, WorkingSide, Ct);

        Assert.Equal(1, mrz.Calls);
        Assert.Equal(photo, mrz.Picture);
        Assert.Null(reading.Result);
        Assert.Equal([MrzQualityCodes.MrzNotFound], reading.Quality);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(-6.0)]
    public async Task ReadAsync_AStraightOrACrookedCard_IsHandedUpright_AtTheSizeOfTheCard_NotThePicture(double degrees)
    {
        using var drawn = BackOnDarkGround();
        using var held = degrees == 0 ? DocumentImageFactory.Brightened(drawn, 0) : DocumentImageFactory.Rotated(drawn, degrees);
        var photo = Png(held);
        var mrz = new ScriptedMrzReader();
        using var picture = Analyzer().Open(photo);
        var report = picture.Inspect(Cie, DocumentSide.Back, Ct);
        Assert.True(DocumentPicture.IsMeasurable(report));

        await CardMrzReader.ReadAsync(mrz, picture, report, photo, WorkingSide, Ct);

        Assert.Equal(1, mrz.Calls);
        using var handed = RasterImage.Decode(Assert.IsType<byte[]>(mrz.Picture), 25_000_000);
        Assert.InRange(handed.Width, 470, 495);
        Assert.InRange(handed.Height, 295, 315);
        var band = Assert.IsType<DocumentFormatRegion>(Cie.Layout.Sides[DocumentSides.Back].MrzRegion);
        var opposite = new DocumentFormatRegion(1 - band.X - band.W, 1 - band.Y - band.H, band.W, band.H);
        Assert.True(DocumentGeometryInspectorTests.InkShare(handed, band) > 1.5 * DocumentGeometryInspectorTests.InkShare(handed, opposite));
    }

    [Fact]
    public async Task ReadCardAsync_ACardLargerThanTheWorkingSize_IsBroughtToItBeforeTheReaderGetsIt()
    {
        var rgb = new byte[3000 * 1890 * 3];
        Array.Fill(rgb, (byte)200);
        using var card = new RasterImage(3000, 1890, rgb);
        var mrz = new ScriptedMrzReader();

        await CardMrzReader.ReadCardAsync(mrz, card, WorkingSide, Ct);

        using var handed = RasterImage.Decode(Assert.IsType<byte[]>(mrz.Picture), 25_000_000);
        Assert.Equal((2000, 1260), (handed.Width, handed.Height));
        Assert.Equal(3000, card.Width);
        Assert.NotEmpty(card.Rgb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadCardAsync_ZeroesThePictureItHandedTheReader_WhetherTheReadingEndsOrBreaks(bool breaks)
    {
        var rgb = new byte[3000 * 1890 * 3];
        new Random(7).NextBytes(rgb);
        using var card = new RasterImage(3000, 1890, rgb);
        var mrz = new ScriptedMrzReader(failure: breaks ? new InvalidOperationException("the reader broke") : null);

        if (breaks)
            await Assert.ThrowsAsync<InvalidOperationException>(() => CardMrzReader.ReadCardAsync(mrz, card, WorkingSide, Ct));
        else
            await CardMrzReader.ReadCardAsync(mrz, card, WorkingSide, Ct);

        Assert.NotNull(mrz.Handed);
        Assert.NotEmpty(mrz.Handed.ToArray());
        Assert.All(mrz.Handed.ToArray(), value => Assert.Equal(0, value));
        Assert.Contains(card.Rgb, value => value != 0);
    }

    [Fact]
    public async Task ReadAsync_LogsNoValueOfTheMrz()
    {
        using var drawn = BackOnDarkGround();
        var photo = Png(drawn);
        var (mrz, _, log) = SpecimenReader();
        using var picture = Analyzer().Open(photo);
        var report = picture.Inspect(Cie, DocumentSide.Back, Ct);

        var reading = await CardMrzReader.ReadAsync(mrz, picture, report, photo, WorkingSide, Ct);

        Assert.True(reading.Result is { CheckDigitsValid: true });
        Assert.NotEmpty(log.Entries);
        foreach (var token in Forbidden)
            Assert.DoesNotContain(token, log.AllOutput, StringComparison.OrdinalIgnoreCase);

        Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(
            typeof(CardMrzReader).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            field => typeof(Microsoft.Extensions.Logging.ILogger).IsAssignableFrom(field.FieldType));
    }

    [Fact]
    public async Task ReadAsync_AnAlteredMrz_IsDoubtful_AndTheCoherenceOfTheDocumentFailsIt()
    {
        using var drawn = BackOnDarkGround();
        var photo = Png(drawn);
        var altered = MrzSpecimens.Td1Line2[..2] + "9" + MrzSpecimens.Td1Line2[3..];
        var (mrz, locator, _) = Reader(MrzSpecimens.Td1Line1, altered, MrzSpecimens.Td1Line3);
        using var picture = Analyzer().Open(photo);
        var report = picture.Inspect(Cie, DocumentSide.Back, Ct);

        var reading = await CardMrzReader.ReadAsync(mrz, picture, report, photo, WorkingSide, Ct);

        var read = Assert.IsType<MrzExtractionResult>(reading.Result);
        Assert.False(read.CheckDigitsValid);
        Assert.Equal(MrzExtractionService.DoubtfulConfidence, read.Confidence);
        Assert.Null(read.DateOfBirth);
        Assert.Equal(1, locator.Calls);
        var coherence = new DocumentCoherenceChecker().Check([], reading, new DocumentCoherenceContext(new DateOnly(2026, 11, 30)), Cie, IdentityTestDocuments.Policy());
        Assert.Equal(DocumentCoherenceResults.Failed, coherence.Summary(DocumentFormatChecks.MrzCheckDigits));
        Assert.False(coherence.AllPassed(Cie.RequiredChecks));
    }

    [Fact]
    public async Task ReadAsync_AnMrzThatCannotBeRead_StaysUnknown_AndNeverPasses()
    {
        using var drawn = BackOnDarkGround();
        var photo = Png(drawn);
        var (mrz, _, _) = Reader();
        using var picture = Analyzer().Open(photo);
        var report = picture.Inspect(Cie, DocumentSide.Back, Ct);

        var reading = await CardMrzReader.ReadAsync(mrz, picture, report, photo, WorkingSide, Ct);

        Assert.Null(reading.Result);
        Assert.Contains(MrzQualityCodes.MrzNotFound, reading.Quality);
        var coherence = new DocumentCoherenceChecker().Check([], reading, new DocumentCoherenceContext(new DateOnly(2026, 11, 30)), Cie, IdentityTestDocuments.Policy());
        Assert.Equal(DocumentCoherenceResults.Unknown, coherence.Summary(DocumentFormatChecks.MrzCheckDigits));
        Assert.False(coherence.AllPassed(Cie.RequiredChecks));
    }

    private sealed class StrokeCountLineReader(params string[] mrzLines) : IMrzTextReader
    {
        public Task<string?> ReadLineAsync(GrayBitmap strip, CancellationToken cancellationToken)
        {
            var middle = strip.Height / 2;
            var strokes = 0;
            var run = 0;
            for (var x = 0; x <= strip.Width; x++)
            {
                var dark = x < strip.Width && strip.Pixels[(middle * strip.Width) + x] < 128;
                if (dark)
                {
                    run++;
                    continue;
                }

                // The cut of the card keeps a pixel or two of the ground at its edge: a dark line that thin is not a stroke.
                if (run >= MinStrokeWidth)
                    strokes++;

                run = 0;
            }

            var index = strokes - MrzStrokes;
            return Task.FromResult(index >= 0 && index < mrzLines.Length ? mrzLines[index] : null);
        }
    }

    private sealed class CountingLocator(IMrzBandLocator inner) : IMrzBandLocator
    {
        public List<(int Width, int Height)> Pages { get; } = [];

        public int Calls => Pages.Count;

        public MrzLocation Locate(GrayBitmap page, CancellationToken cancellationToken)
        {
            Pages.Add((page.Width, page.Height));
            return inner.Locate(page, cancellationToken);
        }
    }
}
