using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public class ZonedFieldReaderNoPersonalDataInLogsTests
{
    private static readonly DocumentFormat Cie = IdentityTestDocuments.Cie();

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

        Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task ZonedFieldReader_NoReadValueInLogs()
    {
        var mrzReading = new MrzReading(IdentityTestDocuments.Mrz(), []);

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

        var timeout = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front))
        {
            var pages = new ZoneScriptedPageReader((_, _, _) => throw new OperationCanceledException(LeakyMessage));
            Assert.Empty(await Reader(timeout, pages, new ScriptedMrzReader()).ReadAsync(card.Image, Cie, DocumentSide.Front));
        }

        AssertNoPersonalData("timeout", timeout);
        Assert.Contains("timeout", timeout.AllOutput, StringComparison.Ordinal);

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

        var unavailable = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front))
        {
            var pages = new ZoneScriptedPageReader((_, _, _) => throw new DocumentReaderUnavailableException("library_missing"));
            Assert.Empty(await Reader(unavailable, pages, new ScriptedMrzReader()).ReadAsync(card.Image, Cie, DocumentSide.Front));
        }

        AssertNoPersonalData("unavailable", unavailable);
        Assert.Contains("library_missing", unavailable.AllOutput, StringComparison.Ordinal);

        var broken = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front))
        {
            var pages = new ZoneScriptedPageReader((_, _, _) => throw new InvalidOperationException(LeakyMessage));
            Assert.Empty(await Reader(broken, pages, new ScriptedMrzReader()).ReadAsync(card.Image, Cie, DocumentSide.Front));
        }

        AssertNoPersonalData("error in the zones", broken);
        Assert.Contains(nameof(InvalidOperationException), broken.AllOutput, StringComparison.Ordinal);

        var brokenMrz = new CapturingLogger<ZonedFieldReader>();
        using (var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back))
        {
            var mrz = new ScriptedMrzReader(failure: new InvalidOperationException(LeakyMessage));
            Assert.Empty(await Reader(brokenMrz, ZoneScriptedPageReader.Answering(new Dictionary<byte, string>()), mrz).ReadAsync(card.Image, Cie, DocumentSide.Back));
        }

        AssertNoPersonalData("error in the MRZ", brokenMrz);
    }
}
