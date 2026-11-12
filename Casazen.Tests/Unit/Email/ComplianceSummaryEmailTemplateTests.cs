using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E03/F02/US02/T02 (NF-52, CHK-12 point 6): the texts of the monthly compliance summary, in Italian and in English. «All compliant» only for a complete month; the
/// open items are counted by the action that fixes them; what is held only by the channel manager is said to be checked there; the email carries counts, names of
/// actions and a link, and every dynamic value is HTML-encoded.
/// </summary>
public class ComplianceSummaryEmailTemplateTests
{
    private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    public static TheoryData<string> Variants => new() { "no-stays", "all-compliant", "open", "held-by-channel-manager" };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Render_ItalianAndEnglish_AreCompleteAndDifferent(string variant)
    {
        var italian = Render(variant, "it-IT");
        var english = Render(variant, "en");

        Assert.Contains("<html lang=\"it\">", italian.HtmlBody);
        Assert.Contains("<html lang=\"en\">", english.HtmlBody);
        Assert.NotEqual(italian.Subject, english.Subject);
        foreach (var content in new[] { italian, english })
        {
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
            Assert.Contains("2026", content.Subject);
        }
    }

    [Fact]
    public void ComplianceSummary_ACompleteMonth_SaysAllCompliantWithTheNumberOfStays()
    {
        var content = EmailTemplates.ComplianceSummary(EmailTemplates.DefaultCulture, Summary(42, Counts(proof: 42), complete: true));

        Assert.Equal("Adempimenti di ottobre 2026: soggiorni 42, tutto in regola", content.Subject);
        Assert.Contains("Tutto in regola: ogni adempimento ha la sua prova archiviata", content.HtmlBody);
        Assert.Contains("Con prova archiviata: 42", content.HtmlBody);
        Assert.DoesNotContain("Non tutto è in regola", content.HtmlBody);
    }

    [Fact]
    public void ComplianceSummary_AnIncompleteMonth_ListsTheMissingByActionInOrder_AndCountsTheStates()
    {
        var summary = Summary(
            10,
            Counts(proof: 5, held: 1, missing: 4, notApplicable: 2),
            complete: false,
            Item(ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceEvidenceClass.Missing),
            Item(ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceEvidenceClass.Missing),
            Item(ComplianceCockpitAction.DeclareTouristTax, ComplianceEvidenceClass.Missing),
            Item(ComplianceCockpitAction.ResolveAlloggiatiFailure, ComplianceEvidenceClass.Missing),
            Item(ComplianceCockpitAction.UploadIstatProof, ComplianceEvidenceClass.ExternalVerificationRequired));

        var content = EmailTemplates.ComplianceSummary(EmailTemplates.DefaultCulture, summary);

        Assert.Equal("Adempimenti di ottobre 2026: soggiorni 10, da sistemare 5", content.Subject);
        var body = content.HtmlBody;
        Assert.Contains("Non tutto è in regola", body);
        Assert.Contains("Ricevute Alloggiati Web da caricare: 2", body);
        Assert.Contains("Imposta di soggiorno da dichiarare e pagare: 1", body);
        Assert.Contains("Comunicazioni Alloggiati Web da correggere: 1", body);
        // What is held by the channel manager is not in the list of the missing: it is counted, and explained.
        Assert.DoesNotContain("Comunicazioni ISTAT da provare", body);
        Assert.Contains("Da verificare nel pannello del channel manager: 1", body);
        Assert.Contains("non lo dà per fatto", body);
        Assert.Contains("Senza prova: 4", body);
        Assert.Contains("Non dovuti (gli ospiti non sono mai arrivati): 2", body);
        Assert.True(
            body.IndexOf("Comunicazioni Alloggiati Web da correggere", StringComparison.Ordinal) < body.IndexOf("Ricevute Alloggiati Web da caricare", StringComparison.Ordinal),
            "The lines are in a fixed order.");
    }

    [Fact]
    public void ComplianceSummary_AnActionNoLineKnows_IsCountedAsAnotherObligation_NeverDropped()
    {
        var summary = Summary(1, Counts(missing: 1), complete: false, Item(null, ComplianceEvidenceClass.Missing));

        var content = EmailTemplates.ComplianceSummary(EmailTemplates.DefaultCulture, summary);

        Assert.Contains("Altri adempimenti da sistemare: 1", content.HtmlBody);
    }

    [Fact]
    public void ComplianceSummary_TheButtonLeadsToTheCompliancePage_AndIsAbsentWhenTheLinkIsNotKnown()
    {
        const string url = "https://casazen-app.test/app/short-rent/compliance";
        var summary = Summary(2, Counts(proof: 2), complete: true);

        var with = EmailTemplates.ComplianceSummary(EmailTemplates.DefaultCulture, summary, url);
        var without = EmailTemplates.ComplianceSummary(EmailTemplates.DefaultCulture, summary);

        Assert.Contains($"href=\"{url}\"", with.HtmlBody);
        Assert.Contains("Apri gli adempimenti", with.HtmlBody);
        Assert.DoesNotContain("href=", without.HtmlBody);
    }

    [Fact]
    public void ComplianceSummary_ANoShowOnlyMonth_IsNoStays_ButStillCountsWhatIsNotOwed()
    {
        var content = EmailTemplates.ComplianceSummary(EmailTemplates.DefaultCulture, Summary(0, Counts(notApplicable: 3), complete: true));

        Assert.Equal("Adempimenti di ottobre 2026: nessun soggiorno", content.Subject);
        Assert.Contains("Non dovuti (gli ospiti non sono mai arrivati): 3", content.HtmlBody);
    }

    [Fact]
    public void ComplianceSummary_NothingOfAGuestOrAProperty_CanBeInIt()
    {
        // The summary has no field for a name, a document, a contact, a booking code or a property: what can be in the text is the month, counts and actions.
        var content = EmailTemplates.ComplianceSummary(
            EmailTemplates.DefaultCulture,
            Summary(3, Counts(proof: 1, held: 1, missing: 1), complete: false, Item(ComplianceCockpitAction.SendAlloggiati, ComplianceEvidenceClass.Missing)),
            "https://casazen-app.test/app/short-rent/compliance");

        Assert.DoesNotContain(Target.ToString("D"), content.HtmlBody);
        Assert.DoesNotMatch(new Regex("@|\\+39|[A-Z0-9]{5}-[A-Z0-9]{5}"), content.Subject + content.HtmlBody.Replace("https://casazen-app.test", string.Empty));
    }

    private static EmailContent Render(string variant, string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        return variant switch
        {
            "no-stays" => EmailTemplates.ComplianceSummary(culture, Summary(0, Counts(), complete: true)),
            "all-compliant" => EmailTemplates.ComplianceSummary(culture, Summary(12, Counts(proof: 12), complete: true)),
            "open" => EmailTemplates.ComplianceSummary(
                culture,
                Summary(12, Counts(proof: 10, missing: 2), complete: false, Item(ComplianceCockpitAction.SendAlloggiati, ComplianceEvidenceClass.Missing), Item(ComplianceCockpitAction.UploadIstatProof, ComplianceEvidenceClass.Missing)),
                "https://casazen-app.test/app/short-rent/compliance"),
            "held-by-channel-manager" => EmailTemplates.ComplianceSummary(
                culture,
                Summary(2, Counts(held: 2), complete: false, Item(ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceEvidenceClass.ExternalVerificationRequired))),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
    }

    private static ComplianceCoverageCounts Counts(int proof = 0, int held = 0, int missing = 0, int notApplicable = 0) =>
        new(proof, held, missing, notApplicable);

    private static ComplianceMonthItem Item(ComplianceCockpitAction? action, ComplianceEvidenceClass evidence) =>
        new(action, Target, "reason", evidence);

    private static ComplianceMonthSummary Summary(int stays, ComplianceCoverageCounts counts, bool complete, params ComplianceMonthItem[] items) =>
        new("2026-10", stays, counts, complete, items);
}
