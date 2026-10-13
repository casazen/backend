using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// AM-02b: the email that tells an administrator that a member asks for access, in Italian and English, with every dynamic
/// value (names, the note) HTML-encoded (FD-13), one link (the people page) and a label for every area and page that can be
/// asked for, in both languages.
/// </summary>
public class OrgAccessRequestEmailTemplatesTests
{
    private const string PeopleUrl = "https://casazen-app.test/app/account/people";
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    [Fact]
    public void OrgAccessRequest_Italian_SaysWhoAsksWithWhichRoleForWhatAndWhereToDecide()
    {
        var content = EmailTemplates.OrgAccessRequest(
            EmailTemplates.DefaultCulture, "Giulia Rinaldi", "Sara Conti", OrgRole.Collaborator, "reports", "Mi serve per il commercialista", PeopleUrl);

        Assert.Equal("Sara Conti chiede di accedere a «Report»", content.Subject);
        Assert.Contains("Ciao Giulia Rinaldi,", content.HtmlBody);
        Assert.Contains("<strong>Sara Conti</strong> (Collaboratore) ha chiesto di accedere a <strong>Report</strong> su CasaZen.", content.HtmlBody);
        Assert.Contains("Il suo messaggio", content.HtmlBody);
        Assert.Contains("Mi serve per il commercialista", content.HtmlBody);
        Assert.Contains("non cambia nulla finché non lo decidi tu", content.HtmlBody);
        Assert.Contains($"href=\"{PeopleUrl}\"", content.HtmlBody);
        Assert.Contains("Apri Persone e permessi", content.HtmlBody);
    }

    [Fact]
    public void OrgAccessRequest_English_IsTranslatedEverywhere()
    {
        var content = EmailTemplates.OrgAccessRequest(
            English, "Giulia Rinaldi", "Sara Conti", OrgRole.Accountant, "billing", "I need the invoices", PeopleUrl);

        Assert.Equal("Sara Conti asks for access to \"Plan and billing\"", content.Subject);
        Assert.Contains("<html lang=\"en\">", content.HtmlBody);
        Assert.Contains("Hello Giulia Rinaldi,", content.HtmlBody);
        Assert.Contains("<strong>Sara Conti</strong> (Accountant) asked for access to <strong>Plan and billing</strong> on CasaZen.", content.HtmlBody);
        Assert.Contains("Their message", content.HtmlBody);
        Assert.Contains("nothing changes until you decide", content.HtmlBody);
        Assert.Contains("Open People and permissions", content.HtmlBody);
        Assert.DoesNotContain("Ciao", content.HtmlBody);
        Assert.DoesNotContain("Apri", content.HtmlBody);
    }

    [Fact]
    public void OrgAccessRequest_WithNoNote_HasNoMessageBlock()
    {
        var withNull = EmailTemplates.OrgAccessRequest(EmailTemplates.DefaultCulture, "A", "B", OrgRole.Collaborator, "people", null, PeopleUrl);
        var withBlank = EmailTemplates.OrgAccessRequest(EmailTemplates.DefaultCulture, "A", "B", OrgRole.Collaborator, "people", "  ", PeopleUrl);

        Assert.DoesNotContain("Il suo messaggio", withNull.HtmlBody);
        Assert.DoesNotContain("Il suo messaggio", withBlank.HtmlBody);
    }

    [Fact]
    public void OrgAccessRequest_EveryDynamicValue_IsHtmlEncoded()
    {
        var content = EmailTemplates.OrgAccessRequest(
            EmailTemplates.DefaultCulture, Payload, Payload, OrgRole.Collaborator, "people", Payload, PeopleUrl);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.Contains(EncodedPayload, content.HtmlBody);
        // The subject is plain text: values are not encoded there, and it is one line.
        Assert.DoesNotContain('\n', content.Subject);
    }

    [Fact]
    public void OrgAccessRequest_TheOnlyLinkIsThePeoplePage()
    {
        var content = EmailTemplates.OrgAccessRequest(
            EmailTemplates.DefaultCulture, "A", "B", OrgRole.Collaborator, "people", "nota", PeopleUrl);

        var links = Regex.Matches(content.HtmlBody, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal([PeopleUrl], links);
    }

    [Fact]
    public void OrgAccessRequest_ALinkThatIsNotAnAbsoluteHttpUrl_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => EmailTemplates.OrgAccessRequest(
            EmailTemplates.DefaultCulture, "A", "B", OrgRole.Collaborator, "people", null, "javascript:alert(1)"));
    }

    [Fact]
    public void OrgAccessRequest_AnAreaOutsideTheList_HasNoLabel()
    {
        Assert.Throws<System.Resources.MissingManifestResourceException>(() => EmailTemplates.OrgAccessRequest(
            EmailTemplates.DefaultCulture, "A", "B", OrgRole.Collaborator, "salaries", null, PeopleUrl));
    }

    // ─── The labels ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("account", "OrgAccessArea_Account")]
    [InlineData("short-rent", "OrgAccessArea_ShortRent")]
    [InlineData("long-rent", "OrgAccessArea_LongRent")]
    [InlineData("supplier", "OrgAccessArea_Supplier")]
    [InlineData("people", "OrgAccessArea_People")]
    [InlineData("integrations", "OrgAccessArea_Integrations")]
    public void OrgAccessAreaKey_IsThePascalCaseOfTheCode(string area, string key)
    {
        Assert.Equal(key, EmailTemplates.OrgAccessAreaKey(area));
    }

    [Fact]
    public void OrgAccessAreaLabel_EveryAreaThatCanBeAskedFor_HasAnItalianAndADifferentEnglishLabel()
    {
        foreach (var area in OrgAccessRequestRules.Areas)
        {
            var italian = EmailTemplates.OrgAccessAreaLabel(EmailTemplates.DefaultCulture, area);
            var english = EmailTemplates.OrgAccessAreaLabel(English, area);

            Assert.False(string.IsNullOrWhiteSpace(italian), area);
            Assert.False(string.IsNullOrWhiteSpace(english), area);
            Assert.NotEqual(italian, english);
        }
    }
}
