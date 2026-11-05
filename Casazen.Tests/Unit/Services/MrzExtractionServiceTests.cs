using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T02 (ADR-013-R10, AC-NF24-1, AC-NF51-3, AC-NF51-4): the MRZ of a passport (TD-3), an ID card (TD-1) and a residence permit
/// (TD-2) is read with every ICAO 9303 check digit, the composite one included; a failed digit is never presented as certain. The text of the
/// lines is the one of the ICAO specimens; the OCR and the locator are replaced by scripts, so these tests prove the rules, not the weights.
/// </summary>
public class MrzExtractionServiceTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static string Replace(string line, int index, char value) => line[..index] + value + line[(index + 1)..];

    [Fact]
    public void Td3_IcaoSpecimen_IsReadWithEveryCheckDigit()
    {
        var result = MrzExtractionService.ParseTd3Mrz(MrzSpecimens.Td3Line1, MrzSpecimens.Td3Line2, Today);

        Assert.NotNull(result);
        Assert.True(result.CheckDigitsValid);
        Assert.Equal(0.95, result.Confidence, 6);
        Assert.Equal("Passport", result.DocumentType);
        Assert.Equal("UTO", result.IssuingState);
        Assert.Equal("ERIKSSON", result.Surname);
        Assert.Equal("ANNA MARIA", result.GivenNames);
        Assert.Equal("L898902C3", result.DocumentNumber);
        Assert.Equal("UTO", result.Nationality);
        Assert.Equal(Utc(1974, 8, 12), result.DateOfBirth);
        Assert.Equal(Utc(2012, 4, 15), result.ExpirationDate);
        Assert.Equal("F", result.Sex);
        Assert.True(result.IsExpired);
    }

    [Fact]
    public void Td1_IcaoSpecimen_IsReadWithEveryCheckDigit()
    {
        var result = MrzExtractionService.ParseTd1Mrz(MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3, Today);

        Assert.NotNull(result);
        Assert.True(result.CheckDigitsValid);
        Assert.Equal("IdCard", result.DocumentType);
        Assert.Equal("ERIKSSON", result.Surname);
        Assert.Equal("ANNA MARIA", result.GivenNames);
        Assert.Equal("D23145890", result.DocumentNumber);
        Assert.Equal("UTO", result.Nationality);
        Assert.Equal(Utc(1974, 8, 12), result.DateOfBirth);
        Assert.Equal(Utc(2012, 4, 15), result.ExpirationDate);
        Assert.Equal("F", result.Sex);
    }

    [Fact]
    public void Td2_IcaoSpecimen_IsReadWithEveryCheckDigit()
    {
        var card = MrzExtractionService.ParseTd2Mrz(MrzSpecimens.Td2Line1, MrzSpecimens.Td2Line2, Today);
        var permit = MrzExtractionService.ParseTd2Mrz("IR" + MrzSpecimens.Td2Line1[2..], MrzSpecimens.Td2Line2, Today);

        Assert.NotNull(card);
        Assert.True(card.CheckDigitsValid);
        Assert.Equal("IdCard", card.DocumentType);
        Assert.Equal("D23145890", card.DocumentNumber);
        Assert.Equal("ERIKSSON", card.Surname);
        Assert.Equal(Utc(1974, 8, 12), card.DateOfBirth);
        Assert.Equal(Utc(2012, 4, 15), card.ExpirationDate);
        Assert.Equal("ResidencePermit", permit!.DocumentType);
    }

    [Fact]
    public void Td2_WithoutAnyDocumentCodeLetter_TakesTheTypeOfItsFormat()
    {
        var result = MrzExtractionService.ParseTd2Mrz("XX" + MrzSpecimens.Td2Line1[2..], MrzSpecimens.Td2Line2, Today);

        Assert.Equal("ResidencePermit", result!.DocumentType);
    }

    [Fact]
    public void WrongLength_IsNotAnMrz()
    {
        Assert.Null(MrzExtractionService.ParseTd3Mrz(MrzSpecimens.Td3Line1[..43], MrzSpecimens.Td3Line2, Today));
        Assert.Null(MrzExtractionService.ParseTd2Mrz(MrzSpecimens.Td2Line1, MrzSpecimens.Td2Line2 + "<", Today));
        Assert.Null(MrzExtractionService.ParseTd1Mrz(MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3[..29], Today));
    }

    [Fact]
    public void FailedBirthDigit_IsNeverCertain_AndTheBirthDateIsNotReturned()
    {
        // The digit after the date of birth is 2: with 3 the date is not the one that was printed.
        var result = MrzExtractionService.ParseTd3Mrz(MrzSpecimens.Td3Line1, Replace(MrzSpecimens.Td3Line2, 19, '3'), Today);

        Assert.NotNull(result);
        Assert.False(result.CheckDigitsValid);
        Assert.Equal(0.40, result.Confidence, 6);
        Assert.Null(result.DateOfBirth);
        Assert.Equal("L898902C3", result.DocumentNumber);
        Assert.Equal(Utc(2012, 4, 15), result.ExpirationDate);
    }

    [Fact]
    public void FailedDocumentNumberDigit_IsNeverCertain_AndTheNumberIsNotReturned()
    {
        var result = MrzExtractionService.ParseTd3Mrz(MrzSpecimens.Td3Line1, Replace(MrzSpecimens.Td3Line2, 9, '5'), Today);

        Assert.NotNull(result);
        Assert.False(result.CheckDigitsValid);
        Assert.Null(result.DocumentNumber);
        Assert.Equal(Utc(1974, 8, 12), result.DateOfBirth);
    }

    [Fact]
    public void FailedExpiryDigit_IsNeverCertain_AndTheExpiryIsNotReturned()
    {
        var result = MrzExtractionService.ParseTd3Mrz(MrzSpecimens.Td3Line1, Replace(MrzSpecimens.Td3Line2, 27, '0'), Today);

        Assert.NotNull(result);
        Assert.False(result.CheckDigitsValid);
        Assert.Null(result.ExpirationDate);
        Assert.Null(result.IsExpired);
    }

    [Fact]
    public void OnlyTheCompositeDigitFailing_IsNeverCertainEither()
    {
        // Every digit of a field is right, the composite one (the last character) is not: the lines are not the ones that were printed.
        var result = MrzExtractionService.ParseTd3Mrz(MrzSpecimens.Td3Line1, Replace(MrzSpecimens.Td3Line2, 43, '9'), Today);

        Assert.NotNull(result);
        Assert.False(result.CheckDigitsValid);
        Assert.Equal(0.40, result.Confidence, 6);
        Assert.Equal("L898902C3", result.DocumentNumber);
        Assert.Equal(Utc(1974, 8, 12), result.DateOfBirth);
    }

    [Fact]
    public void FailedPersonalNumberDigit_IsNeverCertain()
    {
        var result = MrzExtractionService.ParseTd3Mrz(MrzSpecimens.Td3Line1, Replace(MrzSpecimens.Td3Line2, 42, '2'), Today);

        Assert.NotNull(result);
        Assert.False(result.CheckDigitsValid);
    }

    [Fact]
    public void CompositeDigit_OfTheOtherFormats_IsChecked()
    {
        Assert.False(MrzExtractionService.ParseTd1Mrz(MrzSpecimens.Td1Line1, Replace(MrzSpecimens.Td1Line2, 29, '5'), MrzSpecimens.Td1Line3, Today)!.CheckDigitsValid);
        Assert.False(MrzExtractionService.ParseTd2Mrz(MrzSpecimens.Td2Line1, Replace(MrzSpecimens.Td2Line2, 35, '5'), Today)!.CheckDigitsValid);
    }

    [Fact]
    public void NoFieldWithAValidDigit_IsNothingTrustworthy()
    {
        var line2 = Replace(Replace(Replace(MrzSpecimens.Td3Line2, 9, '5'), 19, '3'), 27, '0');

        Assert.Null(MrzExtractionService.ParseTd3Mrz(MrzSpecimens.Td3Line1, line2, Today));
    }

    [Fact]
    public void OcrConfusions_InNumericAndAlphabeticFields_AreCorrectedBeforeTheDigits()
    {
        // The reading has O for 0 and Z for 2 in the date of birth and its digit, I for 1 in the expiry, a zero for the O of the state.
        var line2 = Replace(Replace(Replace(Replace(MrzSpecimens.Td3Line2, 15, 'O'), 19, 'Z'), 21, 'I'), 12, '0');
        var line1 = MrzSpecimens.Td3Line1.Replace("ERIKSSON", "ER1KSSON", StringComparison.Ordinal);

        var result = MrzExtractionService.ParseTd3Mrz(line1, line2, Today);

        Assert.NotNull(result);
        Assert.True(result.CheckDigitsValid);
        Assert.Equal(0.90, result.Confidence, 6);
        Assert.Equal(Utc(1974, 8, 12), result.DateOfBirth);
        Assert.Equal(Utc(2012, 4, 15), result.ExpirationDate);
        Assert.Equal("UTO", result.Nationality);
        Assert.Equal("ERIKSSON", result.Surname);
    }

    [Fact]
    public void LongDocumentNumber_ContinuesInTheOptionalData()
    {
        // A twelve character number: nine in its place, the filler where the digit would be, the rest and its digit in the optional data.
        const string document = "AB1234567CDE";
        var firstLine = "I<UTO" + document[..9] + "<" + document[9..] + MrzSpecimens.Check(document) + new string('<', 11);
        var secondBody = "740812" + MrzSpecimens.Check("740812") + "F" + "300101" + MrzSpecimens.Check("300101") + "UTO" + new string('<', 11);
        var composite = firstLine[5..30] + secondBody[..7] + secondBody[8..15] + secondBody[18..29];
        var secondLine = secondBody + MrzSpecimens.Check(composite);

        var result = MrzExtractionService.ParseTd1Mrz(firstLine, secondLine, MrzSpecimens.Td1Line3, Today);

        Assert.Equal(30, firstLine.Length);
        Assert.Equal(30, secondLine.Length);
        Assert.NotNull(result);
        Assert.True(result.CheckDigitsValid);
        Assert.Equal(document, result.DocumentNumber);
        Assert.False(result.IsExpired);
    }

    [Fact]
    public void Expiry_IsRelativeToToday_InEuropeRome()
    {
        var (line1, valid) = MrzSpecimens.Td3("L898902C3", "740812", "261105");
        var (_, expired) = MrzSpecimens.Td3("L898902C3", "740812", "261104");

        // A document is valid through its expiry date: today it is still valid, the day after it is not.
        Assert.False(MrzExtractionService.ParseTd3Mrz(line1, valid, Today)!.IsExpired);
        Assert.True(MrzExtractionService.ParseTd3Mrz(line1, expired, Today)!.IsExpired);
        Assert.True(MrzExtractionService.ParseTd3Mrz(line1, valid, Today.AddDays(1))!.IsExpired);
    }

    [Theory]
    [InlineData("740812", false, 1974, 8, 12)]
    [InlineData("100101", false, 2010, 1, 1)]
    [InlineData("270101", false, 1927, 1, 1)]
    [InlineData("261105", false, 2026, 11, 5)]
    [InlineData("261106", false, 1926, 11, 6)]
    [InlineData("350415", true, 2035, 4, 15)]
    [InlineData("560101", true, 2056, 1, 1)]
    [InlineData("570101", true, 1957, 1, 1)]
    [InlineData("990101", true, 1999, 1, 1)]
    public void MrzDate_TakesTheCenturyFromTheKindOfDate(string yymmdd, bool isExpiry, int year, int month, int day)
    {
        // A date of birth is never in the future; an expiry date is in 20xx up to 30 years ahead (the old window made 35 into 1935).
        Assert.Equal(Utc(year, month, day), MrzExtractionService.ParseMrzDate(yymmdd, isExpiry, Today));
    }

    [Theory]
    [InlineData("300229", true)]
    [InlineData("991301", false)]
    [InlineData("ABCDEF", false)]
    [InlineData("12345", false)]
    [InlineData("", false)]
    public void MrzDate_ThatIsNotADate_IsNull(string yymmdd, bool isExpiry)
    {
        Assert.Null(MrzExtractionService.ParseMrzDate(yymmdd, isExpiry, Today));
    }

    [Fact]
    public void CheckDigit_FollowsIcao9303()
    {
        Assert.True(MrzExtractionService.ValidateCheckDigit("L898902C3", '6'));
        Assert.True(MrzExtractionService.ValidateCheckDigit("740812", '2'));
        Assert.False(MrzExtractionService.ValidateCheckDigit("740812", '3'));
        Assert.True(MrzExtractionService.ValidateCheckDigit("<<<<<<<<<<<<<<", '<'));
        Assert.False(MrzExtractionService.ValidateCheckDigit("L898902C3", '<'));
        Assert.False(MrzExtractionService.ValidateCheckDigit("L898902C3", 'X'));
        Assert.False(MrzExtractionService.ValidateCheckDigit(string.Empty, '0'));
    }

    [Fact]
    public void CleanLine_KeepsTheCharactersOfAnMrz()
    {
        Assert.Equal("P<UTOERIKSSON<<ANNAMARIA<<<", MrzExtractionService.CleanLine(" p<uto eriksson<<anna maria<<<\nXX"));
        Assert.Equal("P<UTO<", MrzExtractionService.CleanLine("P«UTO＜"));
        Assert.Equal(string.Empty, MrzExtractionService.CleanLine(null));
        Assert.Equal(string.Empty, MrzExtractionService.CleanLine("  \n "));
    }

    [Fact]
    public void Interpret_TellsTheFormatFromTheLines()
    {
        var td1 = MrzExtractionService.Interpret([MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3], Today);
        var td3 = MrzExtractionService.Interpret([MrzSpecimens.Td3Line1, MrzSpecimens.Td3Line2], Today);
        var td2 = MrzExtractionService.Interpret([MrzSpecimens.Td2Line1, MrzSpecimens.Td2Line2], Today);
        var withNoise = MrzExtractionService.Interpret(["SOMETHINGELSE<<NOISE", MrzSpecimens.Td3Line1, MrzSpecimens.Td3Line2], Today);
        var shortLine = MrzExtractionService.Interpret([MrzSpecimens.Td3Line1[..42], MrzSpecimens.Td3Line2], Today);

        Assert.Equal("TD1", td1!.Value.Format);
        Assert.Equal("TD3", td3!.Value.Format);
        Assert.Equal("TD2", td2!.Value.Format);
        Assert.Equal("TD3", withNoise!.Value.Format);
        Assert.True(withNoise.Value.Result.CheckDigitsValid);
        Assert.True(shortLine!.Value.Result.CheckDigitsValid);
        Assert.Null(MrzExtractionService.Interpret(["ABC", "DEF"], Today));
        Assert.Null(MrzExtractionService.Interpret([MrzSpecimens.Td3Line1], Today));
    }

    // ---- the reading of a picture, with the locator and the OCR replaced by scripts -------------------------------------------

    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    private static MrzExtractionService Service(IMrzBandLocator? locator, IMrzTextReader? reader) =>
        new(NullLogger<MrzExtractionService>.Instance, locator, reader, Options.Create(new MrzOcrOptions()), Clock);

    private static MemoryStream Picture() => new(TestImages.Png(32, 24, seed: 9));

    [Fact]
    public async Task ReadAsync_ValidSpecimen_IsReadWithNoHints()
    {
        var locator = new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(3));
        var service = Service(locator, new ScriptedLineReader(MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3));

        var reading = await service.ReadAsync(Picture());

        Assert.NotNull(reading.Result);
        Assert.True(reading.Result.CheckDigitsValid);
        Assert.Equal("D23145890", reading.Result.DocumentNumber);
        Assert.Empty(reading.Quality);
        Assert.Empty(reading.Result.Quality);
        Assert.Equal(1, locator.Calls);
    }

    [Fact]
    public async Task ExtractAsync_IsTheResultOfReadAsync()
    {
        var service = Service(
            new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2)),
            new ScriptedLineReader(MrzSpecimens.Td3Line1, MrzSpecimens.Td3Line2));

        var result = await service.ExtractAsync(Picture());

        Assert.NotNull(result);
        Assert.Equal("Passport", result.DocumentType);
        Assert.True(result.IsExpired);
    }

    [Fact]
    public async Task ReadAsync_NothingFoundInAnyTurn_IsNullAndSaysMrzNotFound()
    {
        var locator = new ScriptedBandLocator(_ => MrzLocation.Empty);
        var service = Service(locator, new ScriptedLineReader("X"));

        var reading = await service.ReadAsync(Picture());

        Assert.Null(reading.Result);
        Assert.Equal(new[] { MrzQualityCodes.MrzNotFound }, reading.Quality);
        Assert.Equal(4, locator.Calls);
    }

    [Fact]
    public async Task ReadAsync_TheDocumentIsOnItsSide_IsFoundInAnotherTurn()
    {
        var locator = new ScriptedBandLocator(call => call == 1 ? MrzLocation.Empty : ScriptedBandLocator.Strips(2));
        var service = Service(locator, new ScriptedLineReader(MrzSpecimens.Td3Line1, MrzSpecimens.Td3Line2));

        var reading = await service.ReadAsync(Picture());

        Assert.NotNull(reading.Result);
        Assert.True(reading.Result.CheckDigitsValid);
        Assert.Equal(2, locator.Calls);
    }

    [Fact]
    public async Task ReadAsync_FailedDigits_IsLowConfidenceAndSaysWhy()
    {
        var locator = new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2, glareShare: 0.2, blurRatio: 0.3));
        var service = Service(locator, new ScriptedLineReader(MrzSpecimens.Td3Line1, Replace(MrzSpecimens.Td3Line2, 19, '3')));

        var reading = await service.ReadAsync(Picture());

        Assert.NotNull(reading.Result);
        Assert.False(reading.Result.CheckDigitsValid);
        Assert.Equal(0.40, reading.Result.Confidence, 6);
        Assert.Equal(new[] { MrzQualityCodes.Glare }, reading.Quality);
        Assert.Equal(reading.Quality, reading.Result.Quality);
    }

    [Fact]
    public async Task ReadAsync_OutOfFocus_SaysBlur()
    {
        var locator = new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2, glareShare: 0, blurRatio: 0.9));
        var service = Service(locator, new ScriptedLineReader("P<UTOXX<<<<<", "123"));

        var reading = await service.ReadAsync(Picture());

        Assert.Null(reading.Result);
        Assert.Equal(new[] { MrzQualityCodes.MrzNotFound }, reading.Quality);

        var readable = Service(locator, new ScriptedLineReader(MrzSpecimens.Td3Line1, Replace(MrzSpecimens.Td3Line2, 19, '3')));
        var doubtful = await readable.ReadAsync(Picture());
        Assert.Equal(new[] { MrzQualityCodes.Blur }, doubtful.Quality);
    }

    [Fact]
    public async Task ReadAsync_ReaderNotAvailable_IsNullWithoutAnError()
    {
        var reader = new ScriptedLineReader("X") { Failure = new DocumentReaderUnavailableException("library_missing") };
        var service = Service(new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2)), reader);

        var reading = await service.ReadAsync(Picture());

        Assert.Null(reading.Result);
        Assert.Empty(reading.Quality);
    }

    [Fact]
    public async Task ReadAsync_NotAPicture_IsNull()
    {
        var service = Service(new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2)), new ScriptedLineReader("X"));

        var reading = await service.ReadAsync(new MemoryStream([1, 2, 3]));

        Assert.Null(reading.Result);
    }

    [Fact]
    public async Task ReadAsync_WithoutLocatorOrReader_ReadsNothing()
    {
        var service = new MrzExtractionService(NullLogger<MrzExtractionService>.Instance);

        var reading = await service.ReadAsync(Picture());

        Assert.Null(reading.Result);
        Assert.Empty(reading.Quality);
    }

    [Fact]
    public async Task ReadAsync_TheCallerCancels_IsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var service = Service(new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2)), new ScriptedLineReader("X"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(Picture(), cancelled.Token));
    }

    [Fact]
    public async Task ReadAsync_TheStrips_AreZeroedWhenItIsDone()
    {
        var locator = new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(3));
        var service = Service(locator, new ScriptedLineReader(MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3));

        await service.ReadAsync(Picture());

        Assert.NotEmpty(locator.Handed);
        Assert.All(locator.Handed, pixels => Assert.All(pixels, p => Assert.Equal(0, p)));
    }

    [Fact]
    public void Options_Defaults_AreUsableAndPinNothing()
    {
        var options = new MrzOcrOptions();

        Assert.Empty(options.Validate());
        Assert.False(options.Mrz.HasWellFormedSha256());
        Assert.False(options.VisualZoneItalian.HasWellFormedSha256());
        Assert.False(options.VisualZoneEnglish.HasWellFormedSha256());
    }

    [Fact]
    public void Options_OutOfRange_AreRefused()
    {
        var options = new MrzOcrOptions { TimeoutSeconds = 0, MaxConcurrency = 100, MrzLanguage = "../x" };

        Assert.True(options.Validate().Count >= 3);
    }
}
