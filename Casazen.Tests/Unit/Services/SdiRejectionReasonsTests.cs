using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E17/F01/US02/T01 (NF-86): the reasons of the SDI in plain words. Every code of the official list «Elenco dei controlli effettuati sul file fattura del Sistema di Interscambio» (versione 2.0,
/// 31/01/2025) has a text of its own in Italian and in English, a code the map does not know says the raw code, and what an SDI notification says is cleaned before it is kept.
/// </summary>
public class SdiRejectionReasonsTests
{
    /// <summary>The 63 codes of the official list (<c>docs/runbooks/fiscal-documents.md</c> says where it comes from), copied here so a code taken out of the map fails a test.</summary>
    private static readonly string[] OfficialCodes =
    [
        "00001", "00002", "00003", "00100", "00101", "00102", "00103", "00104", "00105", "00106", "00107", "00200", "00201",
        "00300", "00301", "00302", "00303", "00304", "00305", "00306", "00311", "00312", "00320", "00321", "00322", "00323", "00324", "00398", "00399",
        "00400", "00401", "00403", "00404", "00409", "00411", "00413", "00414", "00415", "00417", "00418", "00419", "00420", "00421", "00422", "00423",
        "00424", "00425", "00427", "00428", "00429", "00430", "00437", "00438", "00443", "00444", "00445", "00471", "00472", "00473", "00474", "00475",
        "00476", "00477",
    ];

    [Fact]
    public void Map_EveryCodeOfTheOfficialList_HasAReasonOfItsOwn_AndNothingElseIsInTheMapButTheChannel()
    {
        Assert.Equal(63, OfficialCodes.Length);
        Assert.Equal(OfficialCodes.Order(), SdiRejectionReasons.SdiCodes.Order());
        Assert.All(OfficialCodes, code =>
        {
            Assert.True(SdiRejectionReasons.IsKnown(code), code);
            Assert.NotEqual(SdiRejectionReasons.GenericKey, SdiRejectionReasons.KeyOf(code));
        });
        Assert.Equal(OfficialCodes.Length + SdiSubmissionErrors.All.Count, SdiRejectionReasons.KnownCodes.Count);
        Assert.All(SdiSubmissionErrors.All, code => Assert.True(SdiRejectionReasons.IsKnown(code), code));
    }

    [Fact]
    public void Map_EveryKeyIsInTheItalianAndInTheEnglishResourceFile_WithTheSamePlaceholders()
    {
        var italian = Resources("SharedResources.resx");
        var english = Resources("SharedResources.en.resx");
        var keys = SdiRejectionReasons.KnownCodes.Select(SdiRejectionReasons.KeyOf)
            .Concat([SdiRejectionReasons.GenericKey, SdiRejectionReasons.NoCodeKey])
            .Concat(new[] { "SdiNotElectronic", "SdiNotToSend", "SdiStateFinal", "SdiDeclarationInvalid", "FiscalDocumentNotRejected", "FiscalDocumentNotCorrectable", "FiscalDocumentReplacementInvalid", "FiscalDocumentRejectedUseCorrection", "SdiWebhookNotConfigured", "SdiWebhookSecretInvalid", "SdiTransmissionUnknown" })
            .Distinct()
            .ToList();

        foreach (var key in keys)
        {
            Assert.True(italian.TryGetValue(key, out var it) && !string.IsNullOrWhiteSpace(it), $"{key} is not in SharedResources.resx");
            Assert.True(english.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en), $"{key} is not in SharedResources.en.resx");
            Assert.Equal(it!.Contains("{0}", StringComparison.Ordinal), en!.Contains("{0}", StringComparison.Ordinal));
            Assert.NotEqual(it, en);
        }

        // The words never name the provider of the channel (decision D14).
        Assert.All(
            keys,
            key => Assert.DoesNotContain("invoicetronic", italian[key] + english[key], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Describe_AKnownCodeIsItsOwnWords_AnUnknownOneTheGenericWordsWithTheRawCode_NoCodeSaysSo()
    {
        var texts = new ResxDocumentTexts();

        Assert.Contains("partita IVA", SdiRejectionReasons.Describe("00417", texts, "it"), StringComparison.Ordinal);
        Assert.Contains("VAT number", SdiRejectionReasons.Describe("417", texts, "en"), StringComparison.Ordinal);
        Assert.Contains("99999", SdiRejectionReasons.Describe("99999", texts, "it"), StringComparison.Ordinal);
        Assert.Contains("99999", SdiRejectionReasons.Describe(" 99999 ", texts, "en"), StringComparison.Ordinal);
        Assert.Equal(SdiRejectionReasons.NoCodeKey, SdiRejectionReasons.KeyOf(null));
        Assert.Equal(SdiRejectionReasons.NoCodeKey, SdiRejectionReasons.KeyOf("  "));
        Assert.DoesNotContain("SdiReason", SdiRejectionReasons.Describe(null, texts, "it"), StringComparison.Ordinal);
        Assert.Contains("canale di invio", SdiRejectionReasons.Describe(SdiSubmissionErrors.IssuerPending, texts, "it"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("300", "00300")]
    [InlineData(" 00417 ", "00417")]
    [InlineData("12345", "12345")]
    [InlineData("ISSUER_PENDING", "issuer_pending")]
    [InlineData("123456", "123456")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_ANumberOfOneToFiveDigitsIsWrittenWithFive(string? code, string? expected) =>
        Assert.Equal(expected, SdiRejectionReasons.Normalize(code));

    [Fact]
    public void Clean_MasksWhatIsPersonal_CollapsesTheSpaces_AndCutsTheLength()
    {
        Assert.Null(SdiRejectionReasons.Clean(null));
        Assert.Null(SdiRejectionReasons.Clean("  \n\t "));
        Assert.Equal(
            "Cliente *** *** *** non valido",
            SdiRejectionReasons.Clean("Cliente  mario.rossi@example.com\r\nRSSMRA80A01H501U\t12345678901   non valido"));
        Assert.Equal("Codice 00417 e 1234", SdiRejectionReasons.Clean("Codice 00417 e 1234"));

        var cut = SdiRejectionReasons.Clean(new string('a', 900))!;
        Assert.Equal(FiscalDocument.SdiErrorDetailMaxLength, cut.Length);
        Assert.Equal(10, SdiRejectionReasons.Clean(new string('b', 40), 10)!.Length);
    }

    [Fact]
    public void FileNames_FollowTheSpecificationsOfTheSdi_AndTellTwoDocumentsApart()
    {
        var first = Guid.NewGuid();

        var name = SdiFileNames.Of("it", "01234567897", first);

        Assert.Matches("^IT01234567897_[0-9A-Z]{5}\\.xml$", name);
        Assert.Equal(name, SdiFileNames.Of("IT", "01234567897", first));
        Assert.NotEqual(name, SdiFileNames.Of("IT", "01234567897", Guid.NewGuid()));
        Assert.Equal(5, SdiFileNames.Progressive(first).Length);
        // 2000 documents of one transmitter: the progressives are told apart (60 million values: a collision is a matter of luck, ten of them are not).
        Assert.InRange(Enumerable.Range(0, 2000).Select(_ => SdiFileNames.Progressive(Guid.NewGuid())).Distinct().Count(), 1990, 2000);

        Assert.True(SdiFileNames.IsValid("IT01234567897_00001.xml"));
        Assert.True(SdiFileNames.IsValid("IT01234567897_A1b2C.xml"));
        Assert.False(SdiFileNames.IsValid("IT01234567897_000001.xml"));
        Assert.False(SdiFileNames.IsValid("01234567897_00001.xml"));
        Assert.False(SdiFileNames.IsValid("IT01234567897_00001.pdf"));
        Assert.False(SdiFileNames.IsValid(null));
        Assert.Throws<ArgumentException>(() => SdiFileNames.Of("IT", "not a valid transmitter!", first));
    }

    /// <summary>The key → text pairs of a resource file of the web project, read from the repository (the tests never depend on the build output folder).</summary>
    private static Dictionary<string, string> Resources(string fileName)
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        var path = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder."), "Casazen.Web", "Resources", fileName);
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(data => data.Attribute("name")!.Value, data => data.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);
    }
}
