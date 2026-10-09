using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// AM-02: the three emails of an org invitation (the invitation, the reminder of the third day, the note of the expiry), in
/// Italian and English, with every dynamic value HTML-encoded (FD-13) and no personal data in the links.
/// </summary>
public class OrgInvitationEmailTemplatesTests
{
    private const string AcceptUrl = "https://casazen-app.test/invite/accept?token=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PeopleUrl = "https://casazen-app.test/app/account/people";
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";

    // 2026-10-15 11:00 UTC = 13:00 in Rome (daylight saving time).
    private static readonly DateTime ExpiresAt = new(2026, 10, 15, 11, 0, 0, DateTimeKind.Utc);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    // ─── The invitation ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrgInvitation_Italian_SaysWhoInvitesToWhichOrgWithWhichRoleAndWhere()
    {
        var content = EmailTemplates.OrgInvitation(
            EmailTemplates.DefaultCulture, "Anna Leone", "Giulia Rinaldi", "Casa Rossi", OrgRole.Admin,
            ["short-rent", "long-rent"], "anna.leone@example.com", AcceptUrl, ExpiresAt);

        Assert.Equal("Giulia Rinaldi ti invita a lavorare con Casa Rossi su CasaZen", content.Subject);
        Assert.Contains("Ciao Anna Leone,", content.HtmlBody);
        Assert.Contains("Giulia Rinaldi ti ha invitato su CasaZen come <strong>Amministratore</strong> di <strong>Casa Rossi</strong>. Potrai lavorare in: Affitti brevi, Affitti lunghi.", content.HtmlBody);
        Assert.Contains("con l'email <strong>anna.leone@example.com</strong>", content.HtmlBody);
        Assert.Contains("si può usare una sola volta", content.HtmlBody);
        Assert.Contains($"href=\"{AcceptUrl}\"", content.HtmlBody);
        Assert.Contains("Accetta l'invito", content.HtmlBody);
        Assert.Contains("L'invito scade il <strong>15/10/2026 13:00</strong> (ora italiana).", content.HtmlBody);
        Assert.Contains("Se non conosci Giulia Rinaldi, puoi ignorare questa email.", content.HtmlBody);
    }

    [Fact]
    public void OrgInvitation_English_IsTranslatedEverywhere()
    {
        var content = EmailTemplates.OrgInvitation(
            English, "Anna Leone", "Giulia Rinaldi", "Casa Rossi", OrgRole.Accountant,
            ["long-rent"], "anna.leone@example.com", AcceptUrl, ExpiresAt);

        Assert.Equal("Giulia Rinaldi invites you to work with Casa Rossi on CasaZen", content.Subject);
        Assert.Contains("<html lang=\"en\">", content.HtmlBody);
        Assert.Contains("Hello Anna Leone,", content.HtmlBody);
        Assert.Contains("as <strong>Accountant</strong> of <strong>Casa Rossi</strong>. You will work in: Long-term rentals.", content.HtmlBody);
        Assert.Contains("Accept the invitation", content.HtmlBody);
        Assert.Contains("(Italian time)", content.HtmlBody);
        Assert.DoesNotContain("Accetta", content.HtmlBody);
        Assert.DoesNotContain("Ciao", content.HtmlBody);
    }

    [Theory]
    [InlineData(OrgRole.Owner, "Titolare", "Owner")]
    [InlineData(OrgRole.Admin, "Amministratore", "Administrator")]
    [InlineData(OrgRole.PropertyManager, "Property manager", "Property Manager")]
    [InlineData(OrgRole.Collaborator, "Collaboratore", "Collaborator")]
    [InlineData(OrgRole.Accountant, "Contabile", "Accountant")]
    public void OrgRoleLabel_EveryRole_HasAnItalianAndAnEnglishName(OrgRole role, string italian, string english)
    {
        Assert.Equal(italian, EmailTemplates.OrgRoleLabel(EmailTemplates.DefaultCulture, role));
        Assert.Equal(english, EmailTemplates.OrgRoleLabel(English, role));
    }

    [Fact]
    public void OrgRoleLabel_NoRoleOfTheEnumIsLeftWithoutALabel()
    {
        Assert.All(Enum.GetValues<OrgRole>(), role =>
        {
            Assert.False(string.IsNullOrWhiteSpace(EmailTemplates.OrgRoleLabel(EmailTemplates.DefaultCulture, role)), role.ToString());
            Assert.False(string.IsNullOrWhiteSpace(EmailTemplates.OrgRoleLabel(English, role)), role.ToString());
        });
    }

    [Fact]
    public void OrgInvitation_EveryDynamicValue_IsHtmlEncoded()
    {
        var content = EmailTemplates.OrgInvitation(
            EmailTemplates.DefaultCulture, Payload, Payload, Payload, OrgRole.Collaborator,
            ["short-rent"], "anna.leone@example.com", AcceptUrl, ExpiresAt);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.Contains(EncodedPayload, content.HtmlBody);
        // The subject is plain text: values are not encoded there, and it is one line.
        Assert.DoesNotContain('\n', content.Subject);
    }

    [Fact]
    public void OrgInvitation_TheLinkCarriesTheTokenAndNothingElse()
    {
        var content = EmailTemplates.OrgInvitation(
            EmailTemplates.DefaultCulture, "Anna Leone", "Giulia Rinaldi", "Casa Rossi", OrgRole.Collaborator,
            ["short-rent"], "anna.leone@example.com", AcceptUrl, ExpiresAt);

        var links = Regex.Matches(content.HtmlBody, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal([AcceptUrl], links);
        Assert.DoesNotContain("anna.leone%40", string.Join(' ', links));
    }

    [Fact]
    public void OrgInvitation_LinkThatIsNotAnAbsoluteHttpUrl_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => EmailTemplates.OrgInvitation(
            EmailTemplates.DefaultCulture, "A", "B", "C", OrgRole.Collaborator, ["short-rent"], "a@example.com", "javascript:alert(1)", ExpiresAt));
    }

    // ─── The reminder ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrgInvitationReminder_Italian_SaysTheLinkReplacesTheFirstOne()
    {
        var content = EmailTemplates.OrgInvitationReminder(
            EmailTemplates.DefaultCulture, "Anna Leone", "Casa Rossi", OrgRole.Collaborator, AcceptUrl, ExpiresAt);

        Assert.Equal("Promemoria: l'invito a lavorare con Casa Rossi scade il 15/10/2026", content.Subject);
        Assert.Contains("Hai ancora un invito per entrare in <strong>Casa Rossi</strong> su CasaZen come <strong>Collaboratore</strong>", content.HtmlBody);
        Assert.Contains("Questo link sostituisce quello dell'email precedente, che non funziona più.", content.HtmlBody);
        Assert.Contains($"href=\"{AcceptUrl}\"", content.HtmlBody);
        Assert.Contains("15/10/2026 13:00", content.HtmlBody);
    }

    [Fact]
    public void OrgInvitationReminder_English_IsTranslated()
    {
        var content = EmailTemplates.OrgInvitationReminder(English, "Anna Leone", "Casa Rossi", OrgRole.Accountant, AcceptUrl, ExpiresAt);

        Assert.Equal("Reminder: your invitation to work with Casa Rossi expires on 15 October 2026", content.Subject);
        Assert.Contains("This link replaces the one in the previous email, which no longer works.", content.HtmlBody);
        Assert.Contains("as <strong>Accountant</strong>", content.HtmlBody);
    }

    [Fact]
    public void OrgInvitationReminder_EveryDynamicValue_IsHtmlEncoded()
    {
        var content = EmailTemplates.OrgInvitationReminder(
            EmailTemplates.DefaultCulture, Payload, Payload, OrgRole.Collaborator, AcceptUrl, ExpiresAt);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.Contains(EncodedPayload, content.HtmlBody);
    }

    // ─── The expiry note ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrgInvitationExpired_Italian_TellsTheInviterTheSeatIsFreeAndLinksThePeoplePage()
    {
        var content = EmailTemplates.OrgInvitationExpired(
            EmailTemplates.DefaultCulture, "Giulia Rinaldi", "Anna Leone", "anna.leone@example.com", PeopleUrl);

        Assert.Equal("Invito scaduto: Anna Leone", content.Subject);
        Assert.Contains("Ciao Giulia Rinaldi,", content.HtmlBody);
        Assert.Contains("L'invito a <strong>Anna Leone</strong> (anna.leone@example.com) è scaduto senza essere accettato. Il posto nel piano è di nuovo libero.", content.HtmlBody);
        Assert.Contains($"href=\"{PeopleUrl}\"", content.HtmlBody);
        Assert.Contains("Apri Persone e permessi", content.HtmlBody);
        Assert.DoesNotContain("token=", content.HtmlBody);
    }

    [Fact]
    public void OrgInvitationExpired_English_IsTranslatedAndEncoded()
    {
        var content = EmailTemplates.OrgInvitationExpired(English, Payload, Payload, "anna.leone@example.com", PeopleUrl);

        Assert.Equal($"Invitation expired: {Payload}", content.Subject);
        Assert.Contains("expired without being accepted. The seat in your plan is free again.", content.HtmlBody);
        Assert.Contains("Open People and permissions", content.HtmlBody);
        Assert.Contains(EncodedPayload, content.HtmlBody);
        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
    }

    // ─── All of them ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void EveryEmail_IsACompleteDocumentWithoutPlaceholdersLeft(string culture)
    {
        var cultureInfo = CultureInfo.GetCultureInfo(culture);
        var emails = new[]
        {
            EmailTemplates.OrgInvitation(cultureInfo, "Anna", "Giulia", "Casa", OrgRole.PropertyManager, ["short-rent", "long-rent"], "a@example.com", AcceptUrl, ExpiresAt),
            EmailTemplates.OrgInvitationReminder(cultureInfo, "Anna", "Casa", OrgRole.PropertyManager, AcceptUrl, ExpiresAt),
            EmailTemplates.OrgInvitationExpired(cultureInfo, "Giulia", "Anna", "a@example.com", PeopleUrl),
        };

        foreach (var content in emails)
        {
            Assert.StartsWith("<!DOCTYPE html>", content.HtmlBody);
            Assert.Contains($"<html lang=\"{cultureInfo.TwoLetterISOLanguageName}\">", content.HtmlBody);
            Assert.False(string.IsNullOrWhiteSpace(content.Subject));
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
        }
    }

    [Fact]
    public void TemplateNames_AreStableAndDistinct()
    {
        Assert.Equal("org-invitation", EmailTemplates.Names.OrgInvitation);
        Assert.Equal("org-invitation-reminder", EmailTemplates.Names.OrgInvitationReminder);
        Assert.Equal("org-invitation-expired", EmailTemplates.Names.OrgInvitationExpired);
    }
}
