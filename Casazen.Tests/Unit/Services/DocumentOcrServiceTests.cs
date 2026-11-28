using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T02 (AC-NF40-5): the fallback reads the printed fields of a document, among them the two the MRZ does not carry, the place and the
/// date of issue; a value is taken only next to its own label and with a plausible format, and a document with nothing readable gives null so
/// the guided manual entry applies. Never a guess.
/// </summary>
public class DocumentOcrServiceTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    private const string ItalianCard =
        "REPUBBLICA ITALIANA\nCARTA DI IDENTITA\nCOGNOME / SURNAME\nROSSI\nNOME / GIVEN NAMES\nMARIO\n" +
        "DATA DI NASCITA / DATE OF BIRTH 12.03.1980\nSESSO / SEX M\nCITTADINANZA / NATIONALITY ITALIANA\n" +
        "NUMERO DOCUMENTO / DOCUMENT NO CA12345BB\nDATA DI RILASCIO / DATE OF ISSUE 15.01.2021\n" +
        "SCADENZA / DATE OF EXPIRY 15.01.2031\nRILASCIATO DA / ISSUED BY COMUNE DI ROMA\n";

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ParseOcrText_ReadsEveryFieldNextToItsLabel()
    {
        var result = DocumentOcrService.ParseOcrText(ItalianCard, Today);

        Assert.NotNull(result);
        Assert.Equal("MARIO ROSSI", result.FullName);
        Assert.Equal("CA12345BB", result.DocumentNumber);
        Assert.Equal(Utc(1980, 3, 12), result.DateOfBirth);
        Assert.Equal("ITA", result.Nationality);
        Assert.Equal("M", result.Sex);
        Assert.Equal(Utc(2031, 1, 15), result.ExpirationDate);
        Assert.False(result.IsExpired);
        Assert.Equal(Utc(2021, 1, 15), result.DateOfIssue);
        Assert.Equal("COMUNE DI ROMA", result.PlaceOfIssue);
        Assert.Equal(DocumentOcrService.FallbackConfidence, result.Confidence);
        Assert.True(result.Confidence < 0.80, "the guest always confirms what the fallback read");
    }

    [Fact]
    public void ParseOcrText_ValuesOnTheLineOfTheLabelAndOnTheNextOne_AreBothRead()
    {
        const string text = "Place of issue: Questura di Milano\nDate of issue\n3 MAR 2019\nDate of expiry 03/03/2020\nSex F\nNationality FRA";

        var result = DocumentOcrService.ParseOcrText(text, Today);

        Assert.NotNull(result);
        Assert.Equal("QUESTURA DI MILANO", result.PlaceOfIssue);
        Assert.Equal(Utc(2019, 3, 3), result.DateOfIssue);
        Assert.Equal(Utc(2020, 3, 3), result.ExpirationDate);
        Assert.True(result.IsExpired);
        Assert.Equal("F", result.Sex);
        Assert.Equal("FRA", result.Nationality);
    }

    [Fact]
    public void ParseOcrText_AccentsAndCaseOfTheLabelsDoNotMatter()
    {
        const string text = "Data di Nascità: 01-02-1990\nCittadinanza: Italiana";

        var result = DocumentOcrService.ParseOcrText(text, Today);

        Assert.NotNull(result);
        Assert.Equal(Utc(1990, 2, 1), result.DateOfBirth);
        Assert.Equal("ITA", result.Nationality);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n \n")]
    [InlineData("LOREM IPSUM DOLOR SIT AMET\nCONSECTETUR")]
    [InlineData("DATA DI NASCITA / DATE OF BIRTH 45.17.2020")]
    [InlineData("DATA DI NASCITA / DATE OF BIRTH 12.03.2080")]
    [InlineData("NUMERO DOCUMENTO 12\nSESSO Q")]
    public void ParseOcrText_NothingReadable_IsNull(string text)
    {
        Assert.Null(DocumentOcrService.ParseOcrText(text, Today));
    }

    [Fact]
    public void ParseOcrText_ImplausibleValues_AreLeftOut()
    {
        const string text = "COGNOME ROSSI\nDATA DI RILASCIO 01.01.2040\nSCADENZA 01.01.2080\nNUMERO DOCUMENTO ABCDEFGH";

        var result = DocumentOcrService.ParseOcrText(text, Today);

        Assert.NotNull(result);
        Assert.Equal("ROSSI", result.FullName);
        Assert.Null(result.DateOfIssue);
        Assert.Null(result.ExpirationDate);
        Assert.Null(result.DocumentNumber);
    }

    [Fact]
    public void ReadsItalianCardColumnsAndUnlabelledCardNumber()
    {
        const string text = "CARTA DI IDENTITA\nCA12345ZZ\nCOGNOME ROSSI\nNOME ALBA\n"
            + "MUNICIPALITY\nROMA\nLUOGO E DATA DI NASCITA\nROMA (RM) 12.02.1994\n"
            + "SESSO STATURA CITTADINANZA\nSEX HEIGHT NATIONALITY\nF 168 ITA\n"
            + "EMISSIONE / ISSUING    SCADENZA / EXPIRY\n11,03,2022    11.03.2032";

        var result = DocumentOcrService.ParseOcrText(text, Today)!;
        Assert.True(result.HasDocumentEvidence);
        Assert.Equal("IdentityCard", result.DocumentKind);
        Assert.Equal("CA12345ZZ", result.DocumentNumber);
        Assert.Equal("ROMA", result.BirthPlace);
        Assert.Equal("RM", result.BirthProvince);
        Assert.Equal(Utc(1994, 2, 12), result.DateOfBirth);
        Assert.Equal("F", result.Sex);
        Assert.Equal("ITA", result.Nationality);
        Assert.Equal(Utc(2022, 3, 11), result.DateOfIssue);
        Assert.Equal(Utc(2032, 3, 11), result.ExpirationDate);
    }

    [Fact]
    public void IssuingTownAndDateAfterExpiryNeverSupplyTheBirthplace()
    {
        const string text = "CARTA DI IDENTITA\nCOGNOME ROSSI\nNOME ALBA\nSESSO F\n"
            + "EMISSIONE / ISSUING    SCADENZA / EXPIRY\n11.03.2022    11.03.2032\nMILANO (MI) 11.03.2022";
        var read = DocumentOcrService.ParseOcrText(text, Today)!;
        Assert.Null(read.DateOfBirth);
        Assert.Null(read.BirthPlace);
        Assert.Null(read.BirthProvince);
    }

    [Fact]
    public void ReadsResidenceOnlyFromTheResidentialAddressLabel()
    {
        var read = DocumentOcrService.ParseOcrText("INDIRIZZO DI RESIDENZA / RESIDENCE\nVIA ROMA 8\n00100 ROMA (RM)", Today)!;
        Assert.Equal("VIA ROMA 8", read.ResidenceStreet);
        Assert.Equal("00100", read.ResidencePostalCode);
        Assert.Equal("ROMA", read.ResidenceCityName);
        Assert.Equal("RM", read.ResidenceProvince);
        Assert.Null(read.BirthPlace);
        Assert.Null(read.PlaceOfIssue);
        Assert.False(read.HasDocumentEvidence);

        var front = DocumentOcrService.ParseOcrText(ItalianCard, Today)!;
        Assert.Null(front.ResidenceStreet);
        Assert.Null(front.ResidenceCityName);
    }

    [Fact]
    public void ContactDetailsAloneDoNotIdentifyADocument()
    {
        var read = DocumentOcrService.ParseOcrText("COGNOME ROSSI\nNOME ALBA\nDATA DI NASCITA 12.02.1994", Today);
        Assert.NotNull(read);
        Assert.False(read.HasDocumentEvidence);
    }

    [Fact]
    public async Task ExtractAsync_ReadsThePictureFromMemory()
    {
        var service = new DocumentOcrService(
            NullLogger<DocumentOcrService>.Instance, new ScriptedPageReader(ItalianCard), Options.Create(new MrzOcrOptions()), Clock);

        var result = await service.ExtractAsync(new MemoryStream(TestImages.Png(32, 24, seed: 2)));

        Assert.NotNull(result);
        Assert.Equal("COMUNE DI ROMA", result.PlaceOfIssue);
        Assert.Equal(Utc(2021, 1, 15), result.DateOfIssue);
    }

    [Fact]
    public async Task ExtractAsync_RetriesAQuarterTurnWhenTheImageHasNoOrientationTag()
    {
        var reader = new QuarterTurnReader();
        var service = new DocumentOcrService(NullLogger<DocumentOcrService>.Instance, reader, timeProvider: Clock);

        using var photo = new MemoryStream(TestImages.Png(48, 32, seed: 2));
        var result = await service.ExtractAsync(photo);

        Assert.NotNull(result);
        Assert.Equal("ALBA", result.GivenNames);
        Assert.Equal("ROSSI", result.Surname);
        Assert.Equal(3, reader.Attempts);
    }

    [Fact]
    public async Task TimeoutDuringSupplementaryLayoutCannotReturnUncheckedCieDates()
    {
        const int width = 700, height = 470;
        var pixels = new byte[width * height * 3];
        for (int y = 70; y < 390; y++)
        {
            for (int x = 100; x < 600; x++)
                pixels[(y * width + x) * 3 + 2] = 160;
        }
        var service = new DocumentOcrService(NullLogger<DocumentOcrService>.Instance, new InterruptedLayoutReader(), timeProvider: Clock);
        using var photo = new MemoryStream(TestImages.Png(width, height, pixels));

        var result = await service.ObserveAsync(photo);
        Assert.Equal("unreadable", result.Failure);
        Assert.Equal("MARIO", result.Fields?.GivenNames);
        Assert.Null(result.Fields!.DateOfBirth);
        Assert.Null(result.Fields.DateOfIssue);
    }

    [Fact]
    public async Task ObserveAsync_AutomaticReadingMissesTheNames_TheBlockLayoutReadsThem()
    {
        var reader = new NamesOnlyInLayoutReader();
        var service = new DocumentOcrService(NullLogger<DocumentOcrService>.Instance, reader, timeProvider: Clock);
        using var photo = new MemoryStream(TestImages.Png(64, 40, seed: 5));

        var result = await service.ObserveAsync(photo);

        Assert.Equal("ROSSI", result.Fields?.Surname);
        Assert.Equal("MARIO", result.Fields?.GivenNames);
        Assert.Equal("IdentityCard", result.Fields?.DocumentKind);
        Assert.True(reader.LayoutReads > 0);
    }

    [Fact]
    public async Task ObserveAsync_NoLayoutReadingHasTheNames_NoNameIsInvented()
    {
        var reader = new NamesOnlyInLayoutReader { LayoutText = "CARTA DI IDENTITA\nCA12345ZZ" };
        var service = new DocumentOcrService(NullLogger<DocumentOcrService>.Instance, reader, timeProvider: Clock);
        using var photo = new MemoryStream(TestImages.Png(64, 40, seed: 6));

        var result = await service.ObserveAsync(photo);

        Assert.Null(result.Fields?.GivenNames);
        Assert.Null(result.Fields?.Surname);
    }

    private sealed class NamesOnlyInLayoutReader : IPageTextReader
    {
        public string LayoutText { get; init; } = ItalianCard;

        public int LayoutReads { get; private set; }

        public Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("CARTA DI IDENTITA\nCA12345ZZ\nSCADENZA / EXPIRY\n11.03.2032");

        public Task<string?> ReadLayoutAsync(GrayBitmap page, bool sparse, CancellationToken cancellationToken)
        {
            LayoutReads++;
            return Task.FromResult<string?>(LayoutText);
        }
    }

    private sealed class InterruptedLayoutReader : IPageTextReader
    {
        public Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(ItalianCard.Replace("DATA DI RILASCIO", "EMISSIONE", StringComparison.Ordinal));

        public Task<string?> ReadLayoutAsync(GrayBitmap page, bool sparse, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    private sealed class QuarterTurnReader : IPageTextReader
    {
        public int Attempts { get; private set; }

        public Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult<string?>(page.Width < page.Height
                ? "COGNOME / SURNAME\nROSSI\nNOME / NAME =\nALBA"
                : null);
        }
    }

    [Fact]
    public async Task ExtractAsync_NothingReadableOrNoReader_IsNull()
    {
        var unreadable = new DocumentOcrService(
            NullLogger<DocumentOcrService>.Instance, new ScriptedPageReader("LOREM IPSUM"), Options.Create(new MrzOcrOptions()), Clock);
        var unavailable = new DocumentOcrService(
            NullLogger<DocumentOcrService>.Instance,
            new ScriptedPageReader(ItalianCard) { Failure = new DocumentReaderUnavailableException("library_missing") },
            Options.Create(new MrzOcrOptions()),
            Clock);
        var withoutReader = new DocumentOcrService(NullLogger<DocumentOcrService>.Instance);

        Assert.Null(await unreadable.ExtractAsync(new MemoryStream(TestImages.Png(32, 24, seed: 2))));
        Assert.Null(await unavailable.ExtractAsync(new MemoryStream(TestImages.Png(32, 24, seed: 2))));
        Assert.Null(await withoutReader.ExtractAsync(new MemoryStream(TestImages.Png(32, 24, seed: 2))));
        Assert.Null(await unreadable.ExtractAsync(new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public async Task ExtractAsync_TheCallerCancels_IsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var service = new DocumentOcrService(
            NullLogger<DocumentOcrService>.Instance, new ScriptedPageReader(ItalianCard), Options.Create(new MrzOcrOptions()), Clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ExtractAsync(new MemoryStream(TestImages.Png(32, 24, seed: 2)), cancelled.Token));
    }
}
