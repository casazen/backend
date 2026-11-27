using System.Reflection;
using System.Text;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Infrastructure.Identity;
using Casazen.Tests.Unit.Infrastructure.Storage;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E70/F01/US02/T02 (NF-189, NF-191, D1): the registry of the formats of the identity documents. The Italian electronic identity card ships as a file and loads with
/// two sides, a TD1 MRZ on the back and the web channel at <c>consistency</c>; a second format written as a JSON file is found without any change of code; an invalid
/// file is skipped with a code and the valid ones still load; a channel the format does not admit has no assurance (a person decides); nothing a document says is
/// ever logged.
/// </summary>
public sealed class DocumentFormatRegistryTests : IDisposable
{
    private const string CieFileName = "it-cie-3.0.json";

    /// <summary>A passport as a format file: the proof that a format is configuration, not code (D1).</summary>
    private const string PassportText = """
        {
          "id": "it-passport-test",
          "country": "ITA",
          "label": "Passaporto italiano (prova)",
          "documentCodes": ["P"],
          "mrz": { "type": "TD3", "side": "single", "issuingState": "ITA" },
          "sides": ["single"],
          "layout": {
            "aspectRatio": 1.42,
            "aspectTolerance": 0.06,
            "minCardAreaShare": 0.3,
            "marginMin": 0.01,
            "sides": {
              "single": {
                "portrait": { "x": 0.05, "y": 0.2, "w": 0.28, "h": 0.5 },
                "mrzRegion": { "x": 0.02, "y": 0.78, "w": 0.96, "h": 0.2 },
                "fields": [
                  { "name": "surname", "zone": { "x": 0.38, "y": 0.2, "w": 0.55, "h": 0.1 }, "labels": ["COGNOME / SURNAME"] }
                ]
              }
            }
          },
          "requiredChecks": ["layout", "portrait", "mrz_check_digits", "expiry"],
          "assurance": { "web": "consistency" },
          "status": "provisional"
        }
        """;

    private const string OneRegion = """{"x":0.1,"y":0.1,"w":0.2,"h":0.2}""";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-identity-formats-" + Guid.NewGuid().ToString("N"));

    public DocumentFormatRegistryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    public static TheoryData<string, string, string> InvalidFormats()
    {
        var data = new TheoryData<string, string, string>();
        void Add(string name, string code, params (string Path, string? Literal)[] edits)
        {
            data.Add(name + ".json", IdentityPolicyTestFiles.Apply(CieText(), edits), code);
        }

        data.Add("malformed.json", "{ \"id\": ", DocumentFormatReasonCodes.FormatJsonInvalid);
        data.Add("comment.json", "// a comment\n" + CieText(), DocumentFormatReasonCodes.FormatJsonInvalid);
        data.Add("repeated-member.json", CieText().Replace("\"status\": \"provisional\"", "\"status\": \"provisional\", \"status\": \"qualified\"", StringComparison.Ordinal), DocumentFormatReasonCodes.FormatJsonInvalid);
        Add("unknown-member", DocumentFormatReasonCodes.FormatJsonInvalid, ("notes", "\"x\""));
        Add("missing-member", DocumentFormatReasonCodes.FormatJsonInvalid, ("status", null));
        Add("wrong-type", DocumentFormatReasonCodes.FormatJsonInvalid, ("layout.aspectRatio", "\"1.586\""));
        Add("null-member", DocumentFormatReasonCodes.FormatJsonInvalid, ("mrz", "null"));
        Add("id-uppercase", DocumentFormatReasonCodes.FormatIdInvalid, ("id", "\"IT-CIE\""));
        Add("id-empty", DocumentFormatReasonCodes.FormatIdInvalid, ("id", "\"\""));
        Add("id-too-long", DocumentFormatReasonCodes.FormatIdInvalid, ("id", "\"" + new string('a', 41) + "\""));
        Add("country-two-letters", DocumentFormatReasonCodes.FormatCountryInvalid, ("country", "\"IT\""));
        Add("country-lower-case", DocumentFormatReasonCodes.FormatCountryInvalid, ("country", "\"ita\""));
        Add("label-empty", DocumentFormatReasonCodes.FormatLabelInvalid, ("label", "\"\""));
        Add("codes-empty", DocumentFormatReasonCodes.FormatDocumentCodesInvalid, ("documentCodes", "[]"));
        Add("codes-lower-case", DocumentFormatReasonCodes.FormatDocumentCodesInvalid, ("documentCodes", "[\"i\"]"));
        Add("codes-three-letters", DocumentFormatReasonCodes.FormatDocumentCodesInvalid, ("documentCodes", "[\"IDX\"]"));
        Add("codes-repeated", DocumentFormatReasonCodes.FormatDocumentCodesInvalid, ("documentCodes", "[\"I\",\"I\"]"));
        Add("mrz-type", DocumentFormatReasonCodes.FormatMrzInvalid, ("mrz.type", "\"TD4\""));
        Add("mrz-state", DocumentFormatReasonCodes.FormatMrzInvalid, ("mrz.issuingState", "\"ITAL\""));
        Add("mrz-side", DocumentFormatReasonCodes.FormatMrzInvalid, ("mrz.side", "\"top\""));
        Add("sides-reversed", DocumentFormatReasonCodes.FormatSidesInvalid, ("sides", "[\"back\",\"front\"]"));
        Add("sides-front-only", DocumentFormatReasonCodes.FormatSidesInvalid, ("sides", "[\"front\"]"));
        Add("sides-single-and-back", DocumentFormatReasonCodes.FormatSidesInvalid, ("sides", "[\"single\",\"back\"]"));
        Add("mrz-on-a-side-the-format-lacks", DocumentFormatReasonCodes.FormatSidesInvalid, ("mrz.side", "\"single\""));
        Add("aspect-ratio-zero", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.aspectRatio", "0"));
        Add("aspect-tolerance-above-one", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.aspectTolerance", "1.5"));
        Add("card-area-zero", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.minCardAreaShare", "0"));
        Add("margin-half", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.marginMin", "0.5"));
        Add("side-without-layout", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.back", null));
        Add("layout-of-another-side", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.single", "{\"fields\":[]}"));
        Add("mrz-region-on-the-wrong-side", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.front.mrzRegion", OneRegion));
        Add("mrz-region-missing", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.back.mrzRegion", null));
        Add("portrait-missing", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.front.portrait", null));
        Add("portrait-twice", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.back.portrait", OneRegion));
        Add("field-repeated", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.back.fields", $"[{Field("address", OneRegion)},{Field("address", OneRegion)}]"));
        Add("field-without-label", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.back.fields", "[{\"name\":\"address\",\"zone\":" + OneRegion + ",\"labels\":[]}]"));
        Add("field-name-upper-case", DocumentFormatReasonCodes.FormatLayoutInvalid, ("layout.sides.back.fields", $"[{Field("Address", OneRegion)}]"));
        Add("portrait-outside-the-card", DocumentFormatReasonCodes.FormatRegionOutOfBounds, ("layout.sides.front.portrait.x", "0.9"));
        Add("portrait-without-width", DocumentFormatReasonCodes.FormatRegionOutOfBounds, ("layout.sides.front.portrait.w", "0"));
        Add("mrz-region-above-the-card", DocumentFormatReasonCodes.FormatRegionOutOfBounds, ("layout.sides.back.mrzRegion.y", "-0.1"));
        Add("mrz-region-below-the-card", DocumentFormatReasonCodes.FormatRegionOutOfBounds, ("layout.sides.back.mrzRegion.h", "0.5"));
        Add("zone-outside-the-card", DocumentFormatReasonCodes.FormatRegionOutOfBounds, ("layout.sides.back.fields", "[" + Field("address", "{\"x\":0.5,\"y\":0.5,\"w\":0.6,\"h\":0.1}") + "]"));
        Add("checks-empty", DocumentFormatReasonCodes.FormatChecksInvalid, ("requiredChecks", "[]"));
        Add("checks-unknown", DocumentFormatReasonCodes.FormatChecksInvalid, ("requiredChecks", "[\"layout\",\"magic\"]"));
        Add("checks-repeated", DocumentFormatReasonCodes.FormatChecksInvalid, ("requiredChecks", "[\"layout\",\"portrait\",\"layout\"]"));
        Add("assurance-unknown-level", DocumentFormatReasonCodes.FormatAssuranceInvalid, ("assurance.web", "\"magic\""));
        Add("assurance-web-cryptographic", DocumentFormatReasonCodes.FormatAssuranceInvalid, ("assurance.web", "\"cryptographic\""));
        Add("assurance-unknown-channel", DocumentFormatReasonCodes.FormatAssuranceInvalid, ("assurance", "{\"web\":\"consistency\",\"chip\":\"cryptographic\"}"));
        Add("status-unknown", DocumentFormatReasonCodes.FormatStatusInvalid, ("status", "\"approved\""));
        return data;
    }

    [Fact]
    public void Registry_TheCieFileShippedWithTheApi_LoadsWithTwoSidesTd1MrzOnTheBackAndWebConsistency()
    {
        // The production defaults: the folder IdentityFormats of the application, here the build output of the web project.
        var registry = new DocumentFormatRegistry(Options.Create(new IdentityVerificationOptions()), new CapturingLogger<DocumentFormatRegistry>(), AppContext.BaseDirectory);

        var cie = registry.Get("it-cie-3.0");

        Assert.Empty(registry.Rejected);
        Assert.NotNull(cie);
        Assert.Equal(("it-cie-3.0", "ITA", DocumentFormatStatuses.Provisional), (cie.Id, cie.Country, cie.Status));
        Assert.Equal(["front", "back"], cie.Sides);
        Assert.Equal(("TD1", "back", "ITA"), (cie.Mrz.Type, cie.Mrz.Side, cie.Mrz.IssuingState));
        Assert.Equal(new KeyValuePair<string, string>("web", IdentityAssuranceLevels.Consistency), Assert.Single(cie.Assurance));
        Assert.Equal(DocumentFormatChecks.All, cie.RequiredChecks);
        Assert.Equal((1.586, 0.06), (cie.Layout.AspectRatio, cie.Layout.AspectTolerance));
        Assert.Equal(["back", "front"], cie.Layout.Sides.Keys.Order());
        Assert.Equal(registry.All, [cie]);
    }

    [Fact]
    public void Registry_TheCieLayout_HasThePortraitOnTheFrontTheMrzInTheBottomBandOfTheBackAndTheLabelsOfTheCard()
    {
        var cie = new DocumentFormatRegistry(Options.Create(new IdentityVerificationOptions()), new CapturingLogger<DocumentFormatRegistry>(), AppContext.BaseDirectory).Get("it-cie-3.0")!;

        var front = cie.Layout.Sides["front"];
        var back = cie.Layout.Sides["back"];

        Assert.Equal(new DocumentFormatRegion(0.03, 0.22, 0.3, 0.62), front.Portrait);
        Assert.Null(front.MrzRegion);
        Assert.Null(back.Portrait);
        Assert.Equal((0.62, 1.0), (back.MrzRegion!.Y, Math.Round(back.MrzRegion.Y + back.MrzRegion.H, 9)));
        Assert.Equal(
            ["address", "fiscalCode"],
            back.Fields.Select(field => field.Name).Order());
        Assert.Equal(
            ["birthDate", "birthPlace", "documentNumber", "expiryDate", "givenNames", "issueDate", "nationality", "sex", "surname"],
            front.Fields.Select(field => field.Name).Order());

        // The labels as they are printed, in Italian and in English.
        Assert.Contains("COGNOME / SURNAME", front.Fields.Single(field => field.Name == "surname").Labels);
        Assert.Contains("NOME / NAME", front.Fields.Single(field => field.Name == "givenNames").Labels);
        Assert.Contains("SCADENZA / EXPIRY", front.Fields.Single(field => field.Name == "expiryDate").Labels);

        // The identifying fields of the policy are fields of the card.
        var policy = JsonPolicy();
        Assert.All(policy.Document.IdentifyingFields, name => Assert.Contains(cie.Layout.Sides.Values.SelectMany(side => side.Fields), field => field.Name == name));
    }

    [Fact]
    public void DocumentFormatRegistry_NewFormatFile_IsDetectedWithoutCode()
    {
        // Data rule D1: a new format or country is a new file, never a change of code. The two registries differ by one file in a folder.
        Write("without", CieFileName, CieText());
        Write("with", CieFileName, CieText());
        Write("with", "it-passport-test.json", PassportText);
        var registryWithout = Create("without");
        var registryWith = Create("with");

        var passport = registryWith.DetectFromMrz("P<", "ITA", "TD3");

        Assert.Null(registryWithout.DetectFromMrz("P<", "ITA", "TD3"));
        Assert.NotNull(passport);
        Assert.Equal("it-passport-test", passport.Id);
        Assert.Equal(("TD3", "single"), (passport.Mrz.Type, passport.Mrz.Side));
        Assert.Equal("consistency", registryWith.AssuranceFor("it-passport-test", "web:desktop-chromium"));
        Assert.Equal(["it-cie-3.0", "it-passport-test"], registryWith.All.Select(format => format.Id));

        // The card is still the card.
        Assert.Equal("it-cie-3.0", registryWith.DetectFromMrz("I<", "ITA", "TD1")?.Id);
    }

    [Theory]
    [InlineData("I<", "ITA", "TD1")]
    [InlineData("ID", "ITA", "TD1")]
    [InlineData("CI", "ITA", "TD1")]
    [InlineData("C<", "ITA", "TD1")]
    [InlineData(" i< ", "ita", "td1")]
    [InlineData("I", "I<TA", "T D1")]
    public void DetectFromMrz_ACodeOfTheCieAsTheMrzSpellsIt_IsTheCie(string documentCode, string issuingState, string mrzType)
    {
        var registry = ShippedRegistry();

        Assert.Equal("it-cie-3.0", registry.DetectFromMrz(documentCode, issuingState, mrzType)?.Id);
    }

    [Theory]
    [InlineData("ZZ", "ITA", "TD1")]
    [InlineData("PA", "ITA", "TD1")]
    [InlineData("A<", "ITA", "TD1")]
    [InlineData("I<", "UTO", "TD1")]
    [InlineData("I<", "ITA", "TD2")]
    [InlineData("I<", "ITA", "TD3")]
    [InlineData("I<", "ITA", "TD4")]
    [InlineData("<<", "ITA", "TD1")]
    [InlineData("", "ITA", "TD1")]
    [InlineData("I<", "", "TD1")]
    [InlineData("I<", "ITA", "")]
    [InlineData("   ", "ITA", "TD1")]
    [InlineData(null, "ITA", "TD1")]
    [InlineData("I<", null, "TD1")]
    [InlineData("I<", "ITA", null)]
    public void DetectFromMrz_AnUnknownCodeStateOrType_IsNull_AndTheFlowAnswersFormatUnsupported(string? documentCode, string? issuingState, string? mrzType)
    {
        Assert.Null(ShippedRegistry().DetectFromMrz(documentCode, issuingState, mrzType));
    }

    [Fact]
    public void DetectFromMrz_TheMrzSpecimenOfTheIcaoTestsWithTheStateOfTheCard_IsTheCie()
    {
        // The first line of the TD1 specimen of MrzExtractionServiceTests: a document code "I<" for an identity card, the state UTO of ICAO.
        var firstLine = MrzSpecimens.Td1Line1;
        var documentCode = firstLine[..2];
        var specimenState = firstLine[2..5];
        var registry = ShippedRegistry();

        Assert.Equal("I<", documentCode);
        Assert.Null(registry.DetectFromMrz(documentCode, specimenState, "TD1"));
        Assert.Equal("it-cie-3.0", registry.DetectFromMrz(documentCode, "ITA", "TD1")?.Id);
    }

    [Fact]
    public void DetectFromMrz_OfSeveralFormatsOfOneCountry_TheLongestPrefixWins_AndATieIsNoMatch()
    {
        Write("formats", "a.json", Passport(("id", "\"it-a\""), ("documentCodes", "[\"P\"]")));
        Write("formats", "b.json", Passport(("id", "\"it-b\""), ("documentCodes", "[\"PD\"]")));
        Write("formats", "c.json", Passport(("id", "\"it-c\""), ("documentCodes", "[\"P\"]")));
        Write("formats", "d.json", Passport(("id", "\"it-d\""), ("documentCodes", "[\"PX\"]"), ("mrz.issuingState", "\"D\"")));
        var registry = Create("formats");

        Assert.Equal("it-b", registry.DetectFromMrz("PD", "ITA", "TD3")?.Id);

        // Two formats with the prefix P (a and c) cannot be told apart: nobody is guessed.
        Assert.Null(registry.DetectFromMrz("P<", "ITA", "TD3"));
        Assert.Null(registry.DetectFromMrz("PA", "ITA", "TD3"));

        // The MRZ spells the state of Germany "D<<": the filler does not matter.
        Assert.Equal("it-d", registry.DetectFromMrz("PX", "D<<", "TD3")?.Id);
    }

    [Theory]
    [MemberData(nameof(InvalidFormats))]
    public void Registry_AnInvalidFile_IsSkippedWithItsReasonCode_AndTheValidOnesStillLoad(string fileName, string text, string reasonCode)
    {
        var logger = new CapturingLogger<DocumentFormatRegistry>();
        Write("formats", "it-passport-test.json", PassportText);
        Write("formats", fileName, text);

        var registry = Create("formats", logger);

        var rejection = Assert.Single(registry.Rejected);
        Assert.Equal(new DocumentFormatRejection(fileName, reasonCode), rejection);
        Assert.Equal("it-passport-test", Assert.Single(registry.All).Id);
        Assert.NotNull(registry.DetectFromMrz("P<", "ITA", "TD3"));
        Assert.Null(registry.Get("it-cie-3.0"));

        // The log has the file and the code, and nothing a file says: not a number, not a label, not a path.
        var line = Assert.Single(logger.Lines, entry => entry.Contains(reasonCode, StringComparison.Ordinal));
        Assert.Contains(fileName, line, StringComparison.Ordinal);
        var everything = string.Concat(logger.Lines);
        Assert.DoesNotContain(_directory, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("COGNOME", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("0.62", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("1.586", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_AFileThatCannotBeRead_IsSkippedAsUnreadable_AndTheOthersStillLoad()
    {
        // Another process holds the file open: the read fails with an I/O error; that format is not used, the others are.
        var logger = new CapturingLogger<DocumentFormatRegistry>();
        Write("formats", "it-passport-test.json", PassportText);
        Write("formats", CieFileName, CieText());
        using var held = new FileStream(Path.Combine(_directory, "formats", CieFileName), FileMode.Open, FileAccess.Read, FileShare.None);

        var registry = Create("formats", logger);

        Assert.Equal(new DocumentFormatRejection(CieFileName, DocumentFormatReasonCodes.FormatUnreadable), Assert.Single(registry.Rejected));
        Assert.Equal(["it-passport-test"], registry.All.Select(format => format.Id));
        Assert.Null(registry.DetectFromMrz("I<", "ITA", "TD1"));
        Assert.Single(logger.Lines, line => line.Contains(DocumentFormatReasonCodes.FormatUnreadable, StringComparison.Ordinal) && line.Contains(CieFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_TwoFilesOfOneId_AreBothSkipped_AndTheOtherFormatsLoad()
    {
        // Which of the two is meant cannot be told, and the first by name must not shadow the second: neither is used.
        Write("formats", CieFileName, CieText());
        Write("formats", "it-passport-test.json", PassportText);
        Write("formats", "zz-it-passport-test-copy.json", PassportText.Replace("Passaporto italiano (prova)", "Passaporto italiano (copia)", StringComparison.Ordinal));

        var registry = Create("formats");

        Assert.Equal(["it-cie-3.0"], registry.All.Select(format => format.Id));
        Assert.Equal(
            [
                new DocumentFormatRejection("it-passport-test.json", DocumentFormatReasonCodes.FormatIdDuplicate),
                new DocumentFormatRejection("zz-it-passport-test-copy.json", DocumentFormatReasonCodes.FormatIdDuplicate),
            ],
            registry.Rejected.OrderBy(rejection => rejection.FileName, StringComparer.Ordinal));
        Assert.Null(registry.DetectFromMrz("P<", "ITA", "TD3"));
    }

    [Fact]
    public void Registry_ABomAndWindowsLineEndings_AreNoProblem_AndOtherFilesAreIgnored()
    {
        var path = Path.Combine(_directory, "formats", CieFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, CieText().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Write("formats", "notes.txt", "not a format");
        Write("formats", "it-cie-3.0.json.bak", "{");
        Write(Path.Combine("formats", "nested"), "other.json", PassportText);

        var registry = Create("formats");

        Assert.Empty(registry.Rejected);
        Assert.Equal(["it-cie-3.0"], registry.All.Select(format => format.Id));
    }

    [Fact]
    public void Registry_AMissingFolderOrConfigurationThatCannotBeRead_IsEmpty_AndNeverThrows()
    {
        var missingLogger = new CapturingLogger<DocumentFormatRegistry>();
        var missing = Create("does-not-exist", missingLogger);

        var options = new Mock<IOptions<IdentityVerificationOptions>>();
        options.SetupGet(o => o.Value).Throws(new InvalidOperationException("the section could not be bound: 4321"));
        var brokenLogger = new CapturingLogger<DocumentFormatRegistry>();
        var broken = new DocumentFormatRegistry(options.Object, brokenLogger, _directory);

        Assert.Empty(missing.All);
        Assert.Empty(missing.Rejected);
        Assert.Null(missing.DetectFromMrz("I<", "ITA", "TD1"));
        Assert.Null(missing.AssuranceFor("it-cie-3.0", "web"));
        Assert.Contains(missingLogger.Lines, line => line.Contains(DocumentFormatReasonCodes.FormatsFolderMissing, StringComparison.Ordinal));

        Assert.Empty(broken.All);
        Assert.Null(broken.Get("it-cie-3.0"));
        var logged = string.Concat(brokenLogger.Lines);
        Assert.Contains(nameof(InvalidOperationException), logged, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", logged, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_IsReadOnce_AndKeptForTheLifeOfTheProcess()
    {
        Write("formats", CieFileName, CieText());
        var registry = Create("formats");
        var first = registry.All;

        Write("formats", "it-passport-test.json", PassportText);
        File.Delete(Path.Combine(_directory, "formats", CieFileName));

        Assert.Same(first, registry.All);
        Assert.Equal(["it-cie-3.0"], registry.All.Select(format => format.Id));
        Assert.Null(registry.Get("it-passport-test"));
    }

    [Fact]
    public void AssuranceFor_TheWebChannelsOfTheCie_AreConsistency_AndAnyOtherChannelIsNull()
    {
        var registry = ShippedRegistry();

        // Every browser channel of the plan, and the kind alone.
        foreach (var channel in IdentityChannels.All)
            Assert.Equal(IdentityAssuranceLevels.Consistency, registry.AssuranceFor("it-cie-3.0", channel));

        Assert.Equal(IdentityAssuranceLevels.Consistency, registry.AssuranceFor("it-cie-3.0", "web"));
        Assert.Equal(IdentityAssuranceLevels.Consistency, registry.AssuranceFor(" it-cie-3.0 ", " WEB:Other "));

        // A channel the format does not admit has no level: that combination is always reviewed by a person.
        Assert.Null(registry.AssuranceFor("it-cie-3.0", "chip:nfc"));
        Assert.Null(registry.AssuranceFor("it-cie-3.0", "app"));
        Assert.Null(registry.AssuranceFor("it-cie-3.0", ""));
        Assert.Null(registry.AssuranceFor("it-cie-3.0", "  "));
        Assert.Null(registry.AssuranceFor("it-cie-3.0", null));
        Assert.Null(registry.AssuranceFor("it-unknown", "web:ios-safari"));
        Assert.Null(registry.AssuranceFor(null, "web:ios-safari"));
        Assert.Null(registry.AssuranceFor(string.Empty, "web:ios-safari"));
    }

    [Fact]
    public void AssuranceFor_AFormatThatAdmitsNoChannel_IsNullForEveryChannel()
    {
        Write("formats", "it-passport-test.json", Passport(("assurance", "{}")));
        var registry = Create("formats");

        Assert.Empty(registry.Rejected);
        Assert.NotNull(registry.Get("it-passport-test"));
        Assert.All(IdentityChannels.All, channel => Assert.Null(registry.AssuranceFor("it-passport-test", channel)));
    }

    [Theory]
    [InlineData("web:ios-safari", "web")]
    [InlineData("WEB", "web")]
    [InlineData("  web:other ", "web")]
    [InlineData("chip:nfc", "chip")]
    [InlineData("app", "app")]
    public void DocumentFormatChannels_KindOf_IsTheTextBeforeTheColon(string channel, string kind)
    {
        Assert.Equal(kind, DocumentFormatChannels.KindOf(channel));
        Assert.Equal(["web"], DocumentFormatChannels.Kinds);
    }

    [Fact]
    public void Registry_NeverLogsAValueOfADocument_BecauseItNeverSeesOne()
    {
        // Privacy: the registry is asked about the codes of an MRZ, never about its data; whatever it is asked, nothing of it reaches the log.
        var logger = new CapturingLogger<DocumentFormatRegistry>();
        Write("formats", CieFileName, CieText());
        Write("formats", "broken.json", "{");
        var registry = Create("formats", logger);

        _ = registry.DetectFromMrz("SECRET-CODE-77", "SECRET-STATE-88", "SECRET-TYPE-99");
        _ = registry.DetectFromMrz("I<", "ITA", "TD1");
        _ = registry.Get("SECRET-ID-66");
        _ = registry.AssuranceFor("SECRET-ID-66", "SECRET-CHANNEL-55");
        _ = registry.AssuranceFor("it-cie-3.0", "SECRET-CHANNEL-55");

        var everything = string.Concat(logger.Lines);
        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain("SECRET", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, everything, StringComparison.Ordinal);
    }

    [Fact]
    public void WebProject_ShipsTheFormatFilesInTheBuildAndPublishOutput()
    {
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "Casazen.Web.csproj"));

        // The JSON is a Content item in the Web SDK and a None item elsewhere: both are listed.
        foreach (var kind in new[] { "Content", "None" })
        {
            var item = Assert.Single(project.Descendants(kind), element => (string?)element.Attribute("Update") == @"IdentityFormats\**\*.json");
            Assert.Equal("PreserveNewest", (string?)item.Attribute("CopyToOutputDirectory"));
            Assert.Equal("PreserveNewest", (string?)item.Attribute("CopyToPublishDirectory"));
        }

        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "IdentityFormats", CieFileName)), "The CIE file is not in the build output.");
    }

    [Fact]
    public void AddCasazenServices_RegistersTheRegistryAsASingleton_AndTheCommittedConfigurationFindsTheCie()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddJsonFile(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false).Build());
        services.AddLogging();
        services.AddCasazenServices();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<DocumentFormatRegistry>();

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DocumentFormatRegistry)).Lifetime);
        Assert.Same(registry, provider.GetRequiredService<DocumentFormatRegistry>());
        Assert.Equal("it-cie-3.0", registry.DetectFromMrz("I<", "ITA", "TD1")?.Id);
    }

    [Fact]
    public void FormatRecords_HoldNoNumberOfTheirOwn_NoDefaultAndNoConstant()
    {
        // AC5: no layout number is written in C#. A default (but null, the absent region) or a constant would decide in the place of the file.
        Type[] formatTypes =
        [
            typeof(DocumentFormat),
            typeof(DocumentFormatMrz),
            typeof(DocumentFormatLayout),
            typeof(DocumentFormatSideLayout),
            typeof(DocumentFormatField),
            typeof(DocumentFormatRegion),
        ];

        foreach (var type in formatTypes)
        {
            var constructor = Assert.Single(type.GetConstructors());
            Assert.All(constructor.GetParameters(), parameter => Assert.True(!parameter.HasDefaultValue || parameter.DefaultValue is null, $"{type.Name}.{parameter.Name} has a default value."));
            Assert.Empty(type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }
    }

    [Fact]
    public void FormatVocabulary_IsSpelledAsThePlanFixesIt()
    {
        Assert.Equal(["TD1", "TD2", "TD3"], DocumentFormatMrzTypes.All);
        Assert.Equal(["front", "back"], DocumentFormatSides.TwoSided);
        Assert.Equal(["single"], DocumentFormatSides.OneSided);
        Assert.Equal(["front", "back", "single"], DocumentFormatSides.All);
        Assert.Equal(
            ["layout", "portrait", "mrz_check_digits", "expiry", "side_consistency", "field_coherence", "booking_consistency"],
            DocumentFormatChecks.All);
        Assert.Equal(["provisional", "qualified"], DocumentFormatStatuses.All);
        Assert.Equal(
            [
                "format_unreadable", "format_json_invalid", "format_id_invalid", "format_id_duplicate", "format_country_invalid", "format_label_invalid",
                "format_document_codes_invalid", "format_mrz_invalid", "format_sides_invalid", "format_layout_invalid", "format_region_out_of_bounds",
                "format_checks_invalid", "format_assurance_invalid", "format_status_invalid", "formats_folder_missing",
            ],
            DocumentFormatReasonCodes.All);
        Assert.Equal(DocumentFormatReasonCodes.All.Count, DocumentFormatReasonCodes.All.Distinct().Count());
        Assert.Equal(
            typeof(DocumentFormatReasonCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!).Order(),
            DocumentFormatReasonCodes.All.Order());
    }

    private static string Field(string name, string zone)
    {
        return "{\"name\":\"" + name + "\",\"zone\":" + zone + ",\"labels\":[\"LABEL\"]}";
    }

    private static string CieText()
    {
        return File.ReadAllText(Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "IdentityFormats", CieFileName));
    }

    private static string Passport(params (string Path, string? Literal)[] edits)
    {
        return IdentityPolicyTestFiles.Apply(PassportText, edits);
    }

    private static IdentityDecisionPolicy JsonPolicy()
    {
        Assert.True(IdentityConfigurationJson.TryCanonicalize(Encoding.UTF8.GetBytes(IdentityPolicyTestFiles.ShippedText()), out var canonical));
        return IdentityConfigurationJson.ReadStrict<IdentityDecisionPolicy>(canonical)!;
    }

    private static DocumentFormatRegistry ShippedRegistry()
    {
        return new DocumentFormatRegistry(Options.Create(new IdentityVerificationOptions()), new CapturingLogger<DocumentFormatRegistry>(), AppContext.BaseDirectory);
    }

    private void Write(string folder, string fileName, string text)
    {
        var directory = Path.Combine(_directory, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private DocumentFormatRegistry Create(string folder, ILogger<DocumentFormatRegistry>? logger = null)
    {
        var options = Options.Create(new IdentityVerificationOptions { FormatsDirectory = folder });
        return new DocumentFormatRegistry(options, logger ?? new CapturingLogger<DocumentFormatRegistry>(), _directory);
    }
}
