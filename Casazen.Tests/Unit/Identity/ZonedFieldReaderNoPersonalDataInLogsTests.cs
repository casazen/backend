using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US02/T01, privacy rule (NF-190, ADR-013-R6, R19): reading the fields of a document writes no value read on it to any log, at any level, whether the reading
/// succeeds, runs out of time, is cancelled, loses its OCR library or breaks (an error whose message holds the values is not logged either). The logs have codes and
/// counts: the format, the side, how many zones and fields.
/// </summary>
public class ZonedFieldReaderNoPersonalDataInLogsTests
{
    private static readonly DocumentFormat Cie = IdentityTestDocuments.Cie();

    /// <summary>Everything a log must not say about the specimen holder: the values, the text of the zones, the MRZ, in the forms a log would write them.</summary>
    private static readonly string[] Forbidden =
    [
        "ROSSI", "MARIA", "GIUSEPPINA", "CA12345AA", "ROMA", IdentityTestDocuments.FiscalCode,
        "12.03.1985", "1985-03-12", "12/03/1985", "850312", "15.05.2022", "2022-05-15", "12.03.2032", "2032-03-12", "320312",
        "COGNOME", "NUMERO DOCUMENTO", "<<<", "I<ITA",
    ];

    private const string LeakyMessage = "cannot read COGNOME / SURNAME ROSSI NOME MARIA GIUSEPPINA CA12345AA 12.03.1985 I<ITACA12345AA 850312";

    private static readonly (string Field, string Text)[] FrontTexts =
    [
        (ZonedFieldNames.DocumentNumber, "NUMERO DOCUMENTO / DOCUMENT NUMBER\nCA12345AA"),
        (ZonedFieldNames.Surname, "COGNOME / SURNAME\nROSSI"),
        (ZonedFieldNames.GivenNames, "NOME / NAME\nMARIA GIUSEPPINA"),
        (ZonedFieldNames.BirthDate, "LUOGO E DATA DI NASCITA / PLACE AND DATE OF BIRTH\nROMA (RM) 12.03.1985"),
        (ZonedFieldNames.IssueDate, "EMISSIONE / ISSUING\n15.05.2022"),
        (ZonedFieldNames.ExpiryDate, "SCADENZA / EXPIRY\n12.03.2032"),
    ];

    private static ZonedFieldReader Reader(CapturingLogger<ZonedFieldReader> log, IPageTextReader pages, IMrzExtractionService mrz) =>
        new(
            pages,
            mrz,
            log,
            Options.Create(new MrzOcrOptions()),
            new FixedTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero)));

    private static void AssertNoPersonalData(string scenario, CapturingLogger<ZonedFieldReader> log)
    {
        Assert.True(log.Entries.Count > 0, $"{scenario}: the reader logged nothing, so the proof would be empty");
        var output = log.AllOutput;
        foreach (var token in Forbidden)
            Assert.DoesNotContain(token, output, StringComparison.OrdinalIgnoreCase);

        // The exception itself is never handed to the logger: its message could carry the values that were being read.
        Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task ZonedFieldReader_NoReadValueInLogs()
    {
        var mrzReading = new MrzReading(IdentityTestDocuments.Mrz(), []);

        // The reading succeeds on both sides: the values come out of the reader and into none of the logs.
        var success = new CapturingLogger<ZonedFieldReader>();
        using (var front = ZonedFieldTestCard.Of(Cie, DocumentSide.Front))
        using (var back = ZonedFieldTestCard.Of(Cie, DocumentSide.Back))
        {
            var read = await Reader(success, ZoneScriptedPageReader.Answering(front.Script(FrontTexts)), new ScriptedMrzReader(mrzReading))
                .ReadAsync(front.Image, Cie, DocumentSide.Front);
            var machine = await Reader(
                    success,
                    ZoneScriptedPageReader.Answering(back.Script((ZonedFieldNames.FiscalCode, "CODICE FISCALE / FISCAL CODE\n" + IdentityTestDocuments.FiscalCode))),
                    new ScriptedMrzReader(mrzReading))
                .ReadAsync(back.Image, Cie, DocumentSide.Back);
            Assert.Contains(read, field => field.Value == "ROSSI");
            Assert.Contains(machine, field => field.Value == "CA12345AA" && field.Source == ZonedFieldSources.Mrz);
        }

        AssertNoPersonalData("success", success);

        // The OCR runs out of time: the cancellation says what it says, and the log does not repeat it.
        var timeout = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front))
        {
            var pages = new ZoneScriptedPageReader((_, _, _) => throw new OperationCanceledException(LeakyMessage));
            Assert.Empty(await Reader(timeout, pages, new ScriptedMrzReader()).ReadAsync(card.Image, Cie, DocumentSide.Front));
        }

        AssertNoPersonalData("timeout", timeout);
        Assert.Contains("timeout", timeout.AllOutput, StringComparison.Ordinal);

        // The caller gives up.
        var cancelled = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front))
        {
            using var source = new CancellationTokenSource();
            var pages = new ZoneScriptedPageReader((_, _, token) =>
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<string?>(null);
            });
            Assert.Empty(await Reader(cancelled, pages, new ScriptedMrzReader()).ReadAsync(card.Image, Cie, DocumentSide.Front, source.Token));
        }

        AssertNoPersonalData("cancelled", cancelled);

        // The OCR library is not there.
        var unavailable = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front))
        {
            var pages = new ZoneScriptedPageReader((_, _, _) => throw new DocumentReaderUnavailableException("library_missing"));
            Assert.Empty(await Reader(unavailable, pages, new ScriptedMrzReader()).ReadAsync(card.Image, Cie, DocumentSide.Front));
        }

        AssertNoPersonalData("unavailable", unavailable);
        Assert.Contains("library_missing", unavailable.AllOutput, StringComparison.Ordinal);

        // The OCR breaks, in one zone and in every zone, and the message of its error holds what was being read.
        var broken = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front))
        {
            var pages = new ZoneScriptedPageReader((_, _, _) => throw new InvalidOperationException(LeakyMessage));
            Assert.Empty(await Reader(broken, pages, new ScriptedMrzReader()).ReadAsync(card.Image, Cie, DocumentSide.Front));
        }

        AssertNoPersonalData("error in the zones", broken);
        Assert.Contains(nameof(InvalidOperationException), broken.AllOutput, StringComparison.Ordinal);

        // The MRZ reader breaks and the message of its error holds the lines of the MRZ.
        var brokenMrz = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back))
        {
            var mrz = new ScriptedMrzReader(failure: new InvalidOperationException(LeakyMessage));
            Assert.Empty(await Reader(brokenMrz, ZoneScriptedPageReader.Answering(new Dictionary<byte, string>()), mrz).ReadAsync(card.Image, Cie, DocumentSide.Back));
        }

        AssertNoPersonalData("error in the MRZ", brokenMrz);
    }
}
