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
        // A date of issue in the future, an expiry fifty years away and a number with no digit are not taken; the surname alone is.
        const string text = "COGNOME ROSSI\nDATA DI RILASCIO 01.01.2040\nSCADENZA 01.01.2080\nNUMERO DOCUMENTO ABCDEFGH";

        var result = DocumentOcrService.ParseOcrText(text, Today);

        Assert.NotNull(result);
        Assert.Equal("ROSSI", result.FullName);
        Assert.Null(result.DateOfIssue);
        Assert.Null(result.ExpirationDate);
        Assert.Null(result.DocumentNumber);
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
