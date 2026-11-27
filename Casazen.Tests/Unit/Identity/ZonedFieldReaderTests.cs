using System.Text;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

internal static class IdentityTestDocuments
{
    public static readonly DateOnly Today = new(2026, 11, 27);

    public const string Surname = "ROSSI";
    public const string GivenNames = "MARIA GIUSEPPINA";
    public const string DocumentNumber = "CA12345AA";

    public static string FiscalCode { get; } = WithCheckCharacter("RSSMGS85C52H501");

    private static string WithCheckCharacter(string first15) => first15 + ItalianFiscalCode.CheckCharacter(first15);

    public static DocumentFormat Cie()
    {
        var registry = new DocumentFormatRegistry(
            Options.Create(new IdentityVerificationOptions()), new CapturingLogger<DocumentFormatRegistry>(), AppContext.BaseDirectory);
        return registry.Get("it-cie-3.0") ?? throw new InvalidOperationException("The shipped format of the Italian card is not in the test folder.");
    }

    public static IdentityDecisionPolicy Policy()
    {
        Assert.True(IdentityConfigurationJson.TryCanonicalize(Encoding.UTF8.GetBytes(IdentityPolicyTestFiles.ShippedText()), out var canonical));
        return IdentityConfigurationJson.ReadStrict<IdentityDecisionPolicy>(canonical)!;
    }

    public static MrzExtractionResult Mrz(
        string surname = Surname,
        string givenNames = GivenNames,
        string documentNumber = DocumentNumber,
        string birth = "850312",
        string expiry = "320312",
        char sex = 'F',
        char? birthDigit = null,
        string nationality = "ITA")
    {
        var name = surname.Replace(' ', '<') + "<<" + givenNames.Replace(' ', '<');
        var line3 = name.Length > 30 ? name[..30] : name.PadRight(30, '<');
        var line1 = "I<ITA" + documentNumber + MrzSpecimens.Check(documentNumber) + new string('<', 15);
        var head = birth + (birthDigit ?? MrzSpecimens.Check(birth)) + sex + expiry + MrzSpecimens.Check(expiry) + nationality.PadRight(3, '<') + new string('<', 11);
        var composite = line1[5..30] + head[..7] + head[8..15] + head[18..29];
        var line2 = head + MrzSpecimens.Check(composite);
        Assert.All(new[] { line1, line2, line3 }, line => Assert.Equal(30, line.Length));
        return MrzExtractionService.Interpret([line1, line2, line3], Today)?.Result
            ?? throw new InvalidOperationException("The specimen MRZ was not read.");
    }
}

/// <summary>
/// A canonical card (a colour picture) whose field zones are painted, each distinct zone with a gray of its own, so that a fake reader tells from the pixels of a crop
/// which zone it was handed: the tag is the gray of the middle of the crop.
/// </summary>
internal sealed class ZonedFieldTestCard : IDisposable
{
    public const int Width = 1000;
    public const int Height = 630;

    private ZonedFieldTestCard(RasterImage image, IReadOnlyDictionary<string, byte> tags)
    {
        Image = image;
        Tags = tags;
    }

    public RasterImage Image { get; }

    public IReadOnlyDictionary<string, byte> Tags { get; }

    public static ZonedFieldTestCard Of(DocumentFormat format, DocumentSide side, int width = Width, int height = Height)
    {
        var rgb = new byte[width * height * 3];
        Array.Fill(rgb, (byte)255);
        var tags = new Dictionary<string, byte>(StringComparer.Ordinal);
        var byZone = new Dictionary<DocumentFormatRegion, byte>();
        if (format.Layout.Sides.TryGetValue(DocumentSides.CodeOf(side), out var layout))
        {
            foreach (var field in layout.Fields)
            {
                if (!byZone.TryGetValue(field.Zone, out var tag))
                {
                    tag = (byte)(40 + (byZone.Count * 10));
                    byZone[field.Zone] = tag;
                    Paint(rgb, width, height, field.Zone, tag);
                }

                tags[field.Name] = tag;
            }
        }

        return new ZonedFieldTestCard(new RasterImage(width, height, rgb), tags);
    }

    public Dictionary<byte, string> Script(params (string Field, string Text)[] texts) => texts.ToDictionary(text => Tags[text.Field], text => text.Text);

    public void Dispose() => Image.Dispose();

    private static void Paint(byte[] rgb, int width, int height, DocumentFormatRegion zone, byte tag)
    {
        var left = (int)(zone.X * width);
        var right = (int)((zone.X + zone.W) * width);
        var top = (int)(zone.Y * height);
        var bottom = (int)((zone.Y + zone.H) * height);
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
                rgb.AsSpan(((y * width) + x) * 3, 3).Fill(tag);
        }
    }
}

internal sealed class ZoneScriptedPageReader(Func<byte, string, CancellationToken, Task<string?>> answer) : IPageTextReader
{
    public List<(byte Tag, string Mode, int Width, int Height)> Calls { get; } = [];

    public List<byte[]> Handed { get; } = [];

    public static ZoneScriptedPageReader Answering(IReadOnlyDictionary<byte, string> page, IReadOnlyDictionary<byte, string>? layout = null) =>
        new((tag, mode, _) => Task.FromResult(mode == "page" ? page.GetValueOrDefault(tag) : layout?.GetValueOrDefault(tag)));

    public Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken) => Ask(page, "page", cancellationToken);

    public Task<string?> ReadLayoutAsync(GrayBitmap page, bool sparse, CancellationToken cancellationToken) =>
        Ask(page, sparse ? "sparse" : "layout", cancellationToken);

    private Task<string?> Ask(GrayBitmap page, string mode, CancellationToken cancellationToken)
    {
        var tag = page.Pixels[((page.Height / 2) * page.Width) + (page.Width / 2)];
        Calls.Add((tag, mode, page.Width, page.Height));
        Handed.Add(page.Pixels);
        return answer(tag, mode, cancellationToken);
    }
}

internal sealed class ScriptedMrzReader(MrzReading? reading = null, Exception? failure = null) : IMrzExtractionService
{
    public int Calls { get; private set; }

    public byte[]? Picture { get; private set; }

    public MemoryStream? Handed { get; private set; }

    public async Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
        (await ReadAsync(imageStream, cancellationToken)).Result;

    public async Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        Calls++;
        Handed = imageStream as MemoryStream;
        using var copy = new MemoryStream();
        await imageStream.CopyToAsync(copy, cancellationToken);
        Picture = copy.ToArray();
        if (failure is not null)
            throw failure;

        return reading ?? new MrzReading(null, []);
    }
}

public class ZonedFieldReaderTests
{
    private static readonly DocumentFormat Cie = IdentityTestDocuments.Cie();

    private static readonly (string Field, string Text)[] FrontTexts =
    [
        (ZonedFieldNames.DocumentNumber, "NUMERO DOCUMENTO / DOCUMENT NUMBER\nCA12345AA"),
        (ZonedFieldNames.Surname, "COGNOME / SURNAME\nROSSI"),
        (ZonedFieldNames.GivenNames, "NOME / NAME\nMARIA GIUSEPPINA"),
        (ZonedFieldNames.BirthDate, "LUOGO E DATA DI NASCITA / PLACE AND DATE OF BIRTH\nROMA (RM) 12.03.1985"),
        (ZonedFieldNames.Sex, "SESSO / SEX\nF"),
        (ZonedFieldNames.Nationality, "CITTADINANZA / NATIONALITY\nITA"),
        (ZonedFieldNames.IssueDate, "EMISSIONE / ISSUING\n15.05.2022"),
        (ZonedFieldNames.ExpiryDate, "SCADENZA / EXPIRY\n12.03.2032"),
    ];

    private static ZonedFieldReader Reader(
        ZoneScriptedPageReader pages, ScriptedMrzReader? mrz = null, MrzOcrOptions? options = null, CapturingLogger<ZonedFieldReader>? log = null)
    {
        return new ZonedFieldReader(
            pages,
            mrz ?? new ScriptedMrzReader(),
            log ?? new CapturingLogger<ZonedFieldReader>(),
            Options.Create(options ?? new MrzOcrOptions()),
            new FixedTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task ReadAsync_FrontOfTheCard_BindsEachValueToItsZoneAndSide()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var mrz = new ScriptedMrzReader(new MrzReading(IdentityTestDocuments.Mrz(), []));
        var pages = ZoneScriptedPageReader.Answering(card.Script(FrontTexts));

        var fields = await Reader(pages, mrz).ReadAsync(card.Image, Cie, DocumentSide.Front);

        Assert.Equal(
            [
                (ZonedFieldNames.DocumentNumber, "CA12345AA"),
                (ZonedFieldNames.Surname, "ROSSI"),
                (ZonedFieldNames.GivenNames, "MARIA GIUSEPPINA"),
                (ZonedFieldNames.BirthDate, "1985-03-12"),
                (ZonedFieldNames.BirthPlace, "ROMA"),
                (ZonedFieldNames.Sex, "F"),
                (ZonedFieldNames.Nationality, "ITA"),
                (ZonedFieldNames.IssueDate, "2022-05-15"),
                (ZonedFieldNames.ExpiryDate, "2032-03-12"),
            ],
            fields.Select(field => (field.Name, field.Value)));

        Assert.All(fields, field =>
        {
            Assert.Equal(field.Name, field.ZoneId);
            Assert.Equal(DocumentSide.Front, field.Side);
            Assert.Equal(ZonedFieldSources.Viz, field.Source);
            Assert.Equal(DocumentOcrService.FallbackConfidence, field.Confidence);
        });

        Assert.Equal(0, mrz.Calls);
    }

    [Fact]
    public async Task ReadAsync_BirthDateAndBirthPlace_ShareOneZoneAndAreReadOnce()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var pages = ZoneScriptedPageReader.Answering(card.Script(FrontTexts));

        await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front);

        Assert.Equal(card.Tags[ZonedFieldNames.BirthDate], card.Tags[ZonedFieldNames.BirthPlace]);
        Assert.Single(pages.Calls, call => call.Tag == card.Tags[ZonedFieldNames.BirthDate]);
        Assert.Equal(card.Tags.Values.Distinct().Count(), pages.Calls.Select(call => call.Tag).Distinct().Count());
    }

    [Fact]
    public async Task ReadAsync_BackOfTheCard_AddsTheMrzFieldsWithTheirOwnZoneAndConfidence()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back);
        var mrz = new ScriptedMrzReader(new MrzReading(IdentityTestDocuments.Mrz(), []));
        var pages = ZoneScriptedPageReader.Answering(
            card.Script((ZonedFieldNames.FiscalCode, "CODICE FISCALE / FISCAL CODE\n" + IdentityTestDocuments.FiscalCode)));

        var fields = await Reader(pages, mrz).ReadAsync(card.Image, Cie, DocumentSide.Back);

        var machine = fields.Where(field => field.Source == ZonedFieldSources.Mrz).ToList();
        Assert.Equal(
            [
                (ZonedFieldNames.Surname, "ROSSI"),
                (ZonedFieldNames.GivenNames, "MARIA GIUSEPPINA"),
                (ZonedFieldNames.BirthDate, "1985-03-12"),
                (ZonedFieldNames.Sex, "F"),
                (ZonedFieldNames.Nationality, "ITA"),
                (ZonedFieldNames.DocumentNumber, "CA12345AA"),
                (ZonedFieldNames.ExpiryDate, "2032-03-12"),
            ],
            machine.Select(field => (field.Name, field.Value)));
        Assert.All(machine, field =>
        {
            Assert.Equal(ZonedFieldZones.Mrz, field.ZoneId);
            Assert.Equal(DocumentSide.Back, field.Side);
            Assert.Equal(MrzExtractionService.CertainConfidence, field.Confidence);
        });

        var fiscal = Assert.Single(fields, field => field.Source == ZonedFieldSources.Viz);
        Assert.Equal((ZonedFieldNames.FiscalCode, IdentityTestDocuments.FiscalCode, ZonedFieldNames.FiscalCode, DocumentSide.Back), (fiscal.Name, fiscal.Value, fiscal.ZoneId, fiscal.Side));
        Assert.Equal(DocumentOcrService.FallbackConfidence, fiscal.Confidence);
    }

    [Fact]
    public async Task ReadAsync_MrzWithAFailedCheckDigit_KeepsTheLowConfidence_AndDropsTheFieldWhoseDigitFailed()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back);
        var doubtful = IdentityTestDocuments.Mrz(birthDigit: '9');
        Assert.False(doubtful.CheckDigitsValid);
        Assert.Null(doubtful.DateOfBirth);
        var mrz = new ScriptedMrzReader(new MrzReading(doubtful, [MrzQualityCodes.Blur]));

        var fields = await Reader(ZoneScriptedPageReader.Answering(new Dictionary<byte, string>()), mrz).ReadAsync(card.Image, Cie, DocumentSide.Back);

        Assert.DoesNotContain(fields, field => field.Name == ZonedFieldNames.BirthDate);
        Assert.NotEmpty(fields);
        Assert.All(fields, field => Assert.Equal(MrzExtractionService.DoubtfulConfidence, field.Confidence));
    }

    [Fact]
    public async Task ReadAsync_AValueWithoutItsLabel_IsNotReturned()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var pages = ZoneScriptedPageReader.Answering(card.Script(
            (ZonedFieldNames.Surname, "ROSSI"),
            (ZonedFieldNames.GivenNames, "COGNOME / SURNAME\nBIANCHI"),
            (ZonedFieldNames.Sex, "SESSO / SEX"),
            (ZonedFieldNames.ExpiryDate, "SCADENZA / EXPIRY\n12.03.2032")));

        var fields = await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front);

        Assert.Equal([(ZonedFieldNames.ExpiryDate, "2032-03-12")], fields.Select(field => (field.Name, field.Value)));
    }

    [Fact]
    public async Task ReadAsync_APlainTextThatCouldBeAValueOfAnotherFieldZone_IsNotMovedToThatField()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var pages = ZoneScriptedPageReader.Answering(card.Script((ZonedFieldNames.Surname, "NOME / NAME\nMARIA GIUSEPPINA")));

        var fields = await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front);

        Assert.DoesNotContain(fields, field => field.Name is ZonedFieldNames.Surname or ZonedFieldNames.GivenNames);
    }

    [Fact]
    public async Task ReadAsync_AFiscalCodeThatDoesNotCheck_IsNotReturned()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back);
        var wrong = IdentityTestDocuments.FiscalCode[..15] + (IdentityTestDocuments.FiscalCode[15] == 'A' ? 'B' : 'A');
        Assert.False(ItalianFiscalCode.IsValidPersonCode(wrong));
        var pages = ZoneScriptedPageReader.Answering(card.Script((ZonedFieldNames.FiscalCode, "CODICE FISCALE / FISCAL CODE\n" + wrong)));

        var fields = await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Back);

        Assert.Empty(fields);
    }

    [Fact]
    public async Task ReadAsync_TheLayoutReading_IsTriedWhenThePageReadingFindsNothing()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var pages = ZoneScriptedPageReader.Answering(
            new Dictionary<byte, string>(),
            card.Script((ZonedFieldNames.Surname, "COGNOME / SURNAME\nROSSI")));

        var fields = await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front);

        var surname = Assert.Single(fields);
        Assert.Equal((ZonedFieldNames.Surname, "ROSSI"), (surname.Name, surname.Value));
        var surnameCalls = pages.Calls.Where(call => call.Tag == card.Tags[ZonedFieldNames.Surname]).Select(call => call.Mode).ToList();
        Assert.Equal(["page", "layout"], surnameCalls);
    }

    [Fact]
    public async Task ReadAsync_AZoneThatBreaks_IsAbsent_AndTheOtherZonesAreStillRead()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var script = card.Script(FrontTexts);
        var pages = new ZoneScriptedPageReader((tag, _, _) =>
            tag == card.Tags[ZonedFieldNames.Surname]
                ? throw new InvalidOperationException("broken")
                : Task.FromResult(script.GetValueOrDefault(tag)));

        var fields = await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front);

        Assert.DoesNotContain(fields, field => field.Name == ZonedFieldNames.Surname);
        Assert.Contains(fields, field => field.Name == ZonedFieldNames.GivenNames);
        Assert.Contains(fields, field => field.Name == ZonedFieldNames.ExpiryDate);
    }

    [Fact]
    public async Task ReadAsync_TheAddressZone_IsNotRead()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back);
        Assert.Contains("address", card.Tags.Keys);
        var pages = ZoneScriptedPageReader.Answering(card.Script(("address", "INDIRIZZO DI RESIDENZA / RESIDENCE ADDRESS\nVIA ROMA 1 00100 ROMA (RM)")));

        var fields = await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Back);

        Assert.Empty(fields);
        Assert.DoesNotContain(pages.Calls, call => call.Tag == card.Tags["address"]);
    }

    [Fact]
    public async Task ReadAsync_ASideTheFormatHasNoLayoutFor_ReadsNothing()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var mrz = new ScriptedMrzReader(new MrzReading(IdentityTestDocuments.Mrz(), []));
        var pages = ZoneScriptedPageReader.Answering(card.Script(FrontTexts));

        var fields = await Reader(pages, mrz).ReadAsync(card.Image, Cie, DocumentSide.Single);

        Assert.Empty(fields);
        Assert.Empty(pages.Calls);
        Assert.Equal(0, mrz.Calls);
    }

    [Fact]
    public async Task ReadAsync_TheCropIsTheZoneWithASmallMargin()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var pages = ZoneScriptedPageReader.Answering(card.Script((ZonedFieldNames.Surname, "COGNOME / SURNAME\nROSSI")));

        await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front);

        var crop = Assert.Single(pages.Calls, call => call.Tag == card.Tags[ZonedFieldNames.Surname]);
        Assert.InRange(crop.Width, 610, 610 + 24);
        Assert.InRange(crop.Height, 63, 63 + 24);
    }

    [Fact]
    public async Task ReadAsync_TimeoutSeconds_StopsTheReading_AndReturnsWhatWasRead()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var script = card.Script(FrontTexts);
        var pages = new ZoneScriptedPageReader(async (tag, _, token) =>
        {
            if (tag == card.Tags[ZonedFieldNames.GivenNames])
                await Task.Delay(Timeout.Infinite, token);

            return script.GetValueOrDefault(tag);
        });
        var started = DateTime.UtcNow;

        var fields = await Reader(pages, options: new MrzOcrOptions { TimeoutSeconds = 1 }).ReadAsync(card.Image, Cie, DocumentSide.Front);

        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(15));
        Assert.Equal([ZonedFieldNames.DocumentNumber, ZonedFieldNames.Surname], fields.Select(field => field.Name));
    }

    [Fact]
    public async Task ReadAsync_CancelledByTheCaller_ReturnsThePartialList_AndNeverThrows()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        using var source = new CancellationTokenSource();
        var script = card.Script(FrontTexts);
        var pages = new ZoneScriptedPageReader((tag, _, token) =>
        {
            if (tag == card.Tags[ZonedFieldNames.Surname])
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.FromResult(script.GetValueOrDefault(tag));
        });

        var fields = await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front, source.Token);

        Assert.Equal([ZonedFieldNames.DocumentNumber], fields.Select(field => field.Name));
    }

    [Fact]
    public async Task ReadAsync_AReaderThatIsNotAvailable_ReturnsNothing_NotAnException()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var pages = new ZoneScriptedPageReader((_, _, _) => throw new DocumentReaderUnavailableException("library_missing"));

        var fields = await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front);

        Assert.Empty(fields);
        Assert.Single(pages.Calls);
    }

    [Fact]
    public async Task ReadAsync_AnMrzReaderThatBreaks_DoesNotStopTheZones()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back);
        var mrz = new ScriptedMrzReader(failure: new InvalidOperationException("broken"));
        var pages = ZoneScriptedPageReader.Answering(
            card.Script((ZonedFieldNames.FiscalCode, "CODICE FISCALE / FISCAL CODE\n" + IdentityTestDocuments.FiscalCode)));

        var fields = await Reader(pages, mrz).ReadAsync(card.Image, Cie, DocumentSide.Back);

        Assert.Equal([ZonedFieldNames.FiscalCode], fields.Select(field => field.Name));
    }

    [Fact]
    public async Task ReadAsync_TheMrzReaderGetsAPngOfTheWholeCard()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back);
        var mrz = new ScriptedMrzReader(new MrzReading(IdentityTestDocuments.Mrz(), []));

        await Reader(ZoneScriptedPageReader.Answering(new Dictionary<byte, string>()), mrz).ReadAsync(card.Image, Cie, DocumentSide.Back);

        Assert.NotNull(mrz.Picture);
        using var decoded = RasterImage.Decode(mrz.Picture, 25_000_000);
        Assert.Equal((card.Image.Width, card.Image.Height), (decoded.Width, decoded.Height));
        Assert.Equal(card.Image.Rgb, decoded.Rgb);
    }

    [Fact]
    public async Task ReadAsync_ACardWiderThanTheWorkingSide_IsScaledDownBeforeTheMrzReading()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back);
        var mrz = new ScriptedMrzReader(new MrzReading(IdentityTestDocuments.Mrz(), []));

        await Reader(ZoneScriptedPageReader.Answering(new Dictionary<byte, string>()), mrz, new MrzOcrOptions { MaxWorkingSide = 600 })
            .ReadAsync(card.Image, Cie, DocumentSide.Back);

        using var decoded = RasterImage.Decode(mrz.Picture!, 25_000_000);
        Assert.Equal((600, 378), (decoded.Width, decoded.Height));
        Assert.Equal((ZonedFieldTestCard.Width, ZonedFieldTestCard.Height), (card.Image.Width, card.Image.Height));
        Assert.Contains(card.Image.Rgb, value => value != 0);
    }

    [Fact]
    public async Task ReadAsync_ZeroesTheCropsAndThePictureOfTheMrz_AndLeavesTheCardOfTheCaller()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Back);
        var mrz = new ScriptedMrzReader(new MrzReading(IdentityTestDocuments.Mrz(), []));
        var pages = ZoneScriptedPageReader.Answering(
            card.Script((ZonedFieldNames.FiscalCode, "CODICE FISCALE / FISCAL CODE\n" + IdentityTestDocuments.FiscalCode)));

        await Reader(pages, mrz).ReadAsync(card.Image, Cie, DocumentSide.Back);

        Assert.NotEmpty(pages.Handed);
        Assert.All(pages.Handed, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));
        Assert.NotNull(mrz.Handed);
        Assert.NotEmpty(mrz.Handed.ToArray());
        Assert.All(mrz.Handed.ToArray(), value => Assert.Equal(0, value));
        Assert.Contains(card.Image.Rgb, value => value != 0);
    }

    [Fact]
    public async Task ReadAsync_ZeroesTheCropsEvenWhenTheReadingFails()
    {
        using var card = ZonedFieldTestCard.Of(Cie, DocumentSide.Front);
        var pages = new ZoneScriptedPageReader((_, _, _) => throw new InvalidOperationException("broken"));

        await Reader(pages).ReadAsync(card.Image, Cie, DocumentSide.Front);

        Assert.NotEmpty(pages.Handed);
        Assert.All(pages.Handed, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));
    }

    [Fact]
    public void Contracts_TheNamesTheSourcesAndTheZone_AreSpelledAsThePlanFixesThem()
    {
        Assert.Equal(
            ["surname", "givenNames", "birthDate", "birthPlace", "sex", "nationality", "documentNumber", "issueDate", "expiryDate", "fiscalCode"],
            ZonedFieldNames.All);
        Assert.Equal(["mrz", "viz"], ZonedFieldSources.All);
        Assert.Equal("mrz", ZonedFieldZones.Mrz);
        Assert.Equal(ZonedFieldNames.All.Count, ZonedFieldNames.All.Distinct().Count());

        var layoutNames = Cie.Layout.Sides.Values.SelectMany(side => side.Fields).Select(field => field.Name).Where(name => name != "address");
        Assert.All(layoutNames, name => Assert.Contains(name, ZonedFieldNames.All));
    }

    [Fact]
    public void ToString_NeverShowsTheValue()
    {
        var field = new ZonedField(ZonedFieldNames.Surname, "ROSSI", DocumentSide.Front, ZonedFieldNames.Surname, 0.55, ZonedFieldSources.Viz);

        var text = field.ToString();

        Assert.DoesNotContain("ROSSI", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("surname", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_NeverTouchesTheDisk()
    {
        string[] banned =
        [
            "GetTempPath", "GetTempFileName", "File.Write", "File.Create", "File.Open", "File.Copy", "File.Move", "FileStream", "StreamWriter", "Directory.Create",
            "CreateTempSubdirectory", "WriteAllBytes", "WriteAllText",
        ];
        var root = IdentityPolicyTestFiles.RepositoryRoot();
        foreach (var file in new[] { "ZonedFieldReader.cs", "CardPngEncoder.cs" })
        {
            var code = string.Join('\n', File.ReadAllLines(Path.Combine(root, "Casazen.Infrastructure", "Identity", file)).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (var api in banned)
                Assert.DoesNotContain(api, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Registration_TheReaderIsScoped_AndTheInterfaceIsTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton<IPageTextReader>(ZoneScriptedPageReader.Answering(new Dictionary<byte, string>()));
        services.AddScoped<IMrzExtractionService>(_ => new ScriptedMrzReader());
        services.AddSelfHostedIdentityVerification();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var reader = scope.ServiceProvider.GetRequiredService<IZonedFieldReader>();

        Assert.IsType<ZonedFieldReader>(reader);
        Assert.Same(reader, scope.ServiceProvider.GetRequiredService<ZonedFieldReader>());
        await using var other = provider.CreateAsyncScope();
        Assert.NotSame(reader, other.ServiceProvider.GetRequiredService<IZonedFieldReader>());
    }
}
