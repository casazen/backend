using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CF-BE: the privacy notice <c>2026-10-v2</c> names ConcordatoFacile as a recipient of the data (the guided completion of the agreed-rent
/// contracts), in Italian and in English, as the product owner ordered; the app never names it, the legal texts do. The notice is a new
/// version: <c>2026-10-v1</c> is not touched (it is the proof of what was accepted), the two languages have the same sections, it is
/// published like the other drafts, and the day it is activated the hosts must accept it again, the privacy only (PL-02). The text is a
/// draft for a lawyer's review, like the others (<see cref="LegalDocumentTextsTests"/>).
/// </summary>
public class LegalPrivacyNoticeV2Tests
{
    private const string V2 = "2026-10-v2";
    private static readonly string[] Languages = ["it", "en"];

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    public void V2_NamesConcordatoFacileAsARecipient_AndV1StaysAsItWas(string language)
    {
        var v2 = ReadRaw(V2, language);

        Assert.Contains("ConcordatoFacile", v2);
        // The version the hosts accepted is not edited (the runbook, the header of the file): it does not know the recipient.
        Assert.DoesNotContain("ConcordatoFacile", ReadRaw(LegalTextFixtures.DraftVersion, language));
        // The default framing (R24): an independent controller, not a subprocessor, so it is not in the DPA.
        Assert.DoesNotContain(
            "ConcordatoFacile",
            File.ReadAllText(Path.Combine(LegalTextFixtures.ShippedRoot(), "dpa", $"{LegalTextFixtures.DraftVersion}.{language}.html")));
    }

    [Fact]
    public void V2_ItalianAndEnglish_HaveTheSameSections_NumberedWithoutGaps()
    {
        var italian = ReadRaw(V2, "it");
        var english = ReadRaw(V2, "en");

        Assert.Equal(Headings(italian), Headings(english));
        Assert.Equal(ParagraphNumbers(italian), ParagraphNumbers(english));
        Assert.Equal(Count(italian, "<tr>"), Count(english, "<tr>"));
        Assert.Equal(Count(italian, "<table>"), Count(english, "<table>"));
        Assert.Equal(
            LegalDocumentTemplate.TokensIn(italian).Order().ToArray(),
            LegalDocumentTemplate.TokensIn(english).Order().ToArray());
        Assert.Equal(Links(italian), Links(english));

        foreach (var language in Languages)
        {
            var raw = ReadRaw(V2, language);
            var numbers = ParagraphNumbers(raw);
            Assert.Equal(Enumerable.Range(1, Headings(raw).Count).Select(n => n.ToString()), Headings(raw));
            Assert.Equal(numbers.Count, numbers.Distinct().Count());
            foreach (var group in numbers.GroupBy(n => n.Split('.')[0]))
            {
                var minor = group.Select(n => n.Split('.').Length > 1 ? int.Parse(n.Split('.')[1]) : 0).Where(m => m > 0).ToList();
                Assert.Equal(Enumerable.Range(1, minor.Count), minor);
            }

            // As the other drafts: no provider of the dynamic list is named, and no statute is cited that was not verified.
            foreach (var provider in new[] { "Supabase", "Auth0", "Resend", "Expo", "Railway", "Vercel", "DeepSeek", "SendGrid" })
                Assert.DoesNotMatch($@"\b{provider}\b", raw);
            foreach (Match act in Regex.Matches(raw, @"(?:D\.Lgs\.|Legislative Decree|Regolamento \(UE\)|Regulation \(EU\))\s*(?<ref>\d+/\d+)"))
                Assert.Contains(act.Groups["ref"].Value, new[] { "206/2005", "2016/679", "2019/1150" });
        }
    }

    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    public void V2_CarriesTheDraftNoticeAsMetadata_AndItsVersion(string language)
    {
        var raw = ReadRaw(V2, language).TrimStart();

        Assert.StartsWith("<!--", raw);
        Assert.Contains(
            language == "it"
                ? "Bozza redatta da un agente AI su incarico del PO: richiede revisione di un legale prima dell'uso in produzione."
                : "Draft written by an AI agent on behalf of the product owner: it requires review by a lawyer before use in production.",
            raw);
        Assert.Contains(V2, raw);
    }

    [Fact]
    public void V2_IsPublishedWithTheControllerData_WithNoPlaceholderLeftToTheReader()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        settings["Legal:Documents:Privacy:Version"] = V2;
        var service = new LegalDocumentService(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<LegalDocumentService>.Instance);

        Assert.True(service.GetPublication(LegalDocumentKind.Privacy).IsPublished);
        foreach (var language in Languages)
        {
            var html = service.GetText(LegalDocumentKind.Privacy, language)!.Html;

            Assert.Equal(language, service.GetText(LegalDocumentKind.Privacy, language)!.Language);
            Assert.Contains("ConcordatoFacile", html);
            Assert.Contains("Test Rentals S.r.l.", html);
            Assert.DoesNotContain("{{", html);
            Assert.DoesNotContain("<!--", html);
        }
    }

    /// <summary>
    /// The effect on the hosts, the one the product owner accepted ("a new recipient is the purpose"): from the day the privacy version is
    /// set to <c>2026-10-v2</c> on an environment, a host who accepted the notice of the version before must accept this one, and nothing
    /// else changes for it (the Terms and the DPA stay at their versions); once accepted it is not asked again.
    /// </summary>
    [Fact]
    public void V2_WhenActivated_HostsMustAcceptThePrivacyAgain_AndOnlyThePrivacy()
    {
        var settings = LegalTextFixtures.CompleteConfiguration();
        foreach (var key in new[] { "Tos", "Privacy", "Dpa" })
            settings[$"Legal:Documents:{key}:Version"] = LegalTextFixtures.DraftVersion;
        var before = new LegalDocumentService(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), NullLogger<LegalDocumentService>.Instance);
        settings["Legal:Documents:Privacy:Version"] = V2;
        var after = new LegalDocumentService(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), NullLogger<LegalDocumentService>.Instance);

        var acceptedBefore = Snapshot(LegalTextFixtures.DraftVersion, LegalTextFixtures.DraftVersion);
        var acceptedNow = Snapshot(LegalTextFixtures.DraftVersion, V2);

        Assert.True(HostOnboardingGate.Evaluate(acceptedBefore, before).ConsentsAccepted);
        Assert.False(HostOnboardingGate.Evaluate(acceptedBefore, after).ConsentsAccepted);
        Assert.True(HostOnboardingGate.Evaluate(acceptedNow, after).ConsentsAccepted);
    }

    private static UserAuthorizationSnapshot Snapshot(string tosAndDpaVersion, string privacyVersion) =>
        new(
            Exists: true,
            IsActive: true,
            Role: UserRole.PropertyOwner,
            SupplierOrgId: null,
            Memberships: [],
            OrgId: Guid.NewGuid(),
            OnboardingCompletedAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            AcceptedConsents: new HashSet<string>
            {
                UserAuthorizationSnapshot.ConsentKey(ConsentType.Tos, tosAndDpaVersion),
                UserAuthorizationSnapshot.ConsentKey(ConsentType.Privacy, privacyVersion),
                UserAuthorizationSnapshot.ConsentKey(ConsentType.Dpa, tosAndDpaVersion),
            });

    private static string ReadRaw(string version, string language) =>
        File.ReadAllText(Path.Combine(LegalTextFixtures.ShippedRoot(), "privacy", $"{version}.{language}.html"));

    private static List<string> Headings(string raw) =>
        Regex.Matches(raw, @"<h2>(?<n>\d+)\.").Select(m => m.Groups["n"].Value).ToList();

    private static List<string> ParagraphNumbers(string raw) =>
        Regex.Matches(raw, @"<p><strong>(?<n>\d+(?:\.\d+)*)</strong>").Select(m => m.Groups["n"].Value).ToList();

    private static List<string> Links(string raw) =>
        Regex.Matches(raw, @"href=""(?<h>[^""]+)""").Select(m => m.Groups["h"].Value).ToList();

    private static int Count(string raw, string tag) => Regex.Matches(raw, Regex.Escape(tag)).Count;
}
