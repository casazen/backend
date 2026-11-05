using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T02, privacy rule (AC-NF51-12, ADR-013-R6): reading a document writes no MRZ text, name, number or date of birth to any log, at any
/// level, whether the reading succeeds, fails its check digits, finds nothing or breaks (an error whose message holds the MRZ is not logged
/// either). The logs have counts and codes. The same holds for the reading of the printed fields.
/// </summary>
public class MrzExtractionNoPersonalDataInLogsTests
{
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Everything a log must not say about the specimen person: the lines, the name, the numbers and the dates in the forms a log would write them.</summary>
    private static readonly string[] Forbidden =
    [
        MrzSpecimens.Td3Line1, MrzSpecimens.Td3Line2, MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3,
        "ERIKSSON", "ANNA", "MARIA", "L898902C3", "D23145890", "ZE184226B",
        "740812", "120415", "1974-08-12", "12/08/1974", "08/12/1974", "12.08.1974", "2012-04-15", "15/04/2012", "15.04.2012",
        "<<<",
    ];

    private static MrzExtractionService Service(
        CapturingLogger<MrzExtractionService> log, ScriptedBandLocator locator, IMrzTextReader reader) =>
        new(log, locator, reader, Options.Create(new MrzOcrOptions()), Clock);

    private static MemoryStream Picture() => new(TestImages.Png(32, 24, seed: 4));

    private static void AssertNoPersonalData(string scenario, CapturingLogger<MrzExtractionService> log)
    {
        Assert.True(log.Entries.Count > 0, $"{scenario}: the service logged nothing, so the proof would be empty");
        var output = log.AllOutput;
        foreach (var token in Forbidden)
            Assert.DoesNotContain(token, output, StringComparison.OrdinalIgnoreCase);

        // The exception itself is never handed to the logger: its message could carry the lines that were being read.
        Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task MrzExtraction_NoPersonalDataInLogs_Test()
    {
        // Passport read with every digit right.
        var passport = new CapturingLogger<MrzExtractionService>();
        var passportReading = await Service(
            passport,
            new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2)),
            new ScriptedLineReader(MrzSpecimens.Td3Line1, MrzSpecimens.Td3Line2)).ReadAsync(Picture());
        Assert.True(passportReading.Result!.CheckDigitsValid);
        AssertNoPersonalData("passport", passport);

        // ID card read with every digit right.
        var card = new CapturingLogger<MrzExtractionService>();
        var cardReading = await Service(
            card,
            new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(3)),
            new ScriptedLineReader(MrzSpecimens.Td1Line1, MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3)).ReadAsync(Picture());
        Assert.True(cardReading.Result!.CheckDigitsValid);
        AssertNoPersonalData("id card", card);

        // A reading with a failed digit.
        var doubtful = new CapturingLogger<MrzExtractionService>();
        var doubtfulReading = await Service(
            doubtful,
            new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2)),
            new ScriptedLineReader(MrzSpecimens.Td3Line1, MrzSpecimens.Td3Line2[..19] + "3" + MrzSpecimens.Td3Line2[20..])).ReadAsync(Picture());
        Assert.False(doubtfulReading.Result!.CheckDigitsValid);
        AssertNoPersonalData("failed digit", doubtful);

        // Nothing found.
        var nothing = new CapturingLogger<MrzExtractionService>();
        var nothingReading = await Service(
            nothing,
            new ScriptedBandLocator(_ => MrzLocation.Empty),
            new ScriptedLineReader("X")).ReadAsync(Picture());
        Assert.Null(nothingReading.Result);
        AssertNoPersonalData("nothing found", nothing);

        // The OCR breaks and the message of its error holds the whole MRZ.
        var broken = new CapturingLogger<MrzExtractionService>();
        var reader = new ScriptedLineReader("X")
        {
            Failure = new InvalidOperationException("cannot read " + MrzSpecimens.Td3Line1 + " " + MrzSpecimens.Td3Line2),
        };
        var brokenReading = await Service(broken, new ScriptedBandLocator(_ => ScriptedBandLocator.Strips(2)), reader).ReadAsync(Picture());
        Assert.Null(brokenReading.Result);
        AssertNoPersonalData("error", broken);
    }

    [Fact]
    public void DocumentReading_HasNothingThatCouldWriteAPicture()
    {
        // No database context (so no DocumentImage row, ADR-013-R6), no job queue, no storage, no form file.
        var services = new[]
        {
            typeof(MrzExtractionService), typeof(DocumentOcrService), typeof(MrzImageLocator), typeof(TesseractMrzTextReader),
            typeof(TesseractPageTextReader),
        };
        foreach (var service in services)
        {
            foreach (var parameter in service.GetConstructors().SelectMany(constructor => constructor.GetParameters()))
            {
                var type = parameter.ParameterType;
                Assert.False(typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(type), $"{service.Name} takes a database context");
                Assert.DoesNotContain("Hangfire", type.Namespace ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("Storage", type.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Queue", type.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("FormFile", type.Name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ImagePipeline_NeverTouchesTheDisk()
    {
        // The classes that hold a picture, a crop or a vector: none of them calls an API that creates or writes a file (the model files are
        // written by ModelFileProvisioner, which never sees an image). A temporary file of a document would break ADR-013-R6 and R2.
        string[] files =
        [
            "MrzExtractionService.cs", "DocumentOcrService.cs", "MrzImageLocator.cs", "TesseractOcr.cs", "RasterImage.cs", "ImageBytes.cs",
            "FaceDetector.cs", "FaceEmbedder.cs", "AntiSpoofingService.cs", "BiometricVerificationService.cs",
        ];
        string[] banned =
        [
            "GetTempPath", "GetTempFileName", "File.Write", "File.Create", "File.Open", "File.Copy", "File.Move", "FileStream",
            "StreamWriter", "Directory.Create", "CreateTempSubdirectory", "WriteAllBytes", "WriteAllText",
        ];
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Casazen.sln")))
            root = Path.GetDirectoryName(root);

        Assert.NotNull(root);
        foreach (var file in files)
        {
            var path = Path.Combine(root, "Casazen.Infrastructure", "Services", file);
            var code = File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            var text = string.Join('\n', code);
            foreach (var api in banned)
                Assert.DoesNotContain(api, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DocumentOcr_NoPersonalDataInLogs()
    {
        const string text = "COGNOME / SURNAME\nROSSI\nNOME / GIVEN NAMES\nMARIO\nDATA DI NASCITA / DATE OF BIRTH 12.03.1980\n" +
                            "NUMERO DOCUMENTO / DOCUMENT NO CA12345BB\nDATA DI RILASCIO / DATE OF ISSUE 15.01.2021\n" +
                            "RILASCIATO DA / ISSUED BY COMUNE DI ROMA";
        string[] forbidden = ["ROSSI", "MARIO", "CA12345BB", "12.03.1980", "15.01.2021", "COMUNE DI ROMA", "1980-03-12", "12/03/1980"];

        foreach (var failure in new Exception?[] { null, new InvalidOperationException("cannot read " + text) })
        {
            var log = new CapturingLogger<DocumentOcrService>();
            var service = new DocumentOcrService(log, new ScriptedPageReader(text) { Failure = failure }, Options.Create(new MrzOcrOptions()), Clock);

            var result = await service.ExtractAsync(Picture());

            Assert.Equal(failure is null, result is not null);
            Assert.True(log.Entries.Count > 0);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, log.AllOutput, StringComparison.OrdinalIgnoreCase);

            Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
        }
    }
}
