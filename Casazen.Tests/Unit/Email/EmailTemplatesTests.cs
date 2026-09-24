using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>FD-13 (A4-08, A5-33): localized IT/EN templates where every dynamic value is HTML-encoded.</summary>
public class EmailTemplatesTests
{
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";
    private const string Link = "https://casazen-app.test/app/supplier/inbox";
    private static readonly DateTime CheckIn = new(2026, 10, 5);

    public static TheoryData<string> Cultures => new() { "it-IT", "en" };

    public static TheoryData<string> TemplateNames => new()
    {
        "created", "taken", "completed", "rejected", "invite", "checkin-link", "checkin-incomplete", "alloggiati",
    };

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public void Render_PayloadInEveryDynamicValue_IsHtmlEncoded(string template)
    {
        var content = Render(template, "it-IT", Payload);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.DoesNotContain("phish.example\">", content.HtmlBody);
        Assert.Contains(EncodedPayload, content.HtmlBody);
    }

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public void Render_ItalianAndEnglish_ProduceCompleteLocalizedDocuments(string template)
    {
        var italian = Render(template, "it-IT", "Villa Rosa");
        var english = Render(template, "en", "Villa Rosa");

        Assert.StartsWith("<!DOCTYPE html>", italian.HtmlBody);
        Assert.Contains("<html lang=\"it\">", italian.HtmlBody);
        Assert.Contains("<html lang=\"en\">", english.HtmlBody);
        Assert.Contains("<meta charset=\"utf-8\" />", italian.HtmlBody);
        Assert.Contains("CasaZen — gestione affitti brevi", italian.HtmlBody);
        Assert.Contains("CasaZen — short-term rental management", english.HtmlBody);
        Assert.NotEqual(italian.Subject, english.Subject);
        foreach (var content in new[] { italian, english })
        {
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
            Assert.False(string.IsNullOrWhiteSpace(content.Subject));
        }
    }

    [Fact]
    public void ServiceRequestCreated_Italian_KeepsTheInformationOfThePreviousEmail()
    {
        var content = EmailTemplates.ServiceRequestCreated(
            EmailTemplates.DefaultCulture, "Pulizie Srl", "cleaning", "Villa Rosa", "Chiavi in portineria", Link);

        Assert.Equal("Nuova richiesta di servizio — Villa Rosa", content.Subject);
        Assert.Contains("Ciao Pulizie Srl,", content.HtmlBody);
        Assert.Contains("Hai ricevuto una nuova richiesta di <strong>Pulizie</strong> per la proprietà <strong>Villa Rosa</strong>.", content.HtmlBody);
        Assert.Contains("Note:</strong><br />Chiavi in portineria", content.HtmlBody);
        Assert.Contains($"href=\"{Link}\"", content.HtmlBody);
        Assert.Contains("Apri la console fornitore", content.HtmlBody);
    }

    [Fact]
    public void ServiceCategoryLabel_EveryCategoryCode_HasItalianAndEnglishLabel()
    {
        var italian = CultureInfo.GetCultureInfo("it-IT");
        var english = CultureInfo.GetCultureInfo("en");

        Assert.All(ServiceCategories.All, code =>
        {
            var it = EmailTemplates.ServiceCategoryLabel(italian, code);
            var en = EmailTemplates.ServiceCategoryLabel(english, code);
            Assert.NotEqual(code, it);
            Assert.NotEqual(it, en);
        });
        Assert.Equal("Pulizie", EmailTemplates.ServiceCategoryLabel(italian, ServiceCategories.Cleaning));
        Assert.Equal("Cleaning", EmailTemplates.ServiceCategoryLabel(english, ServiceCategories.Cleaning));
    }

    [Fact]
    public void ServiceRequestStatusChanged_EnglishLegacyCategory_ShowsValueEncoded()
    {
        var content = EmailTemplates.ServiceRequestStatusChanged(
            CultureInfo.GetCultureInfo("en"), ServiceRequestStatus.Completato, "Idraulica <b>speciale</b>", "Villa");

        Assert.Contains("Idraulica &lt;b&gt;speciale&lt;/b&gt;", content.HtmlBody);
    }

    [Fact]
    public void ServiceRequestCreated_EmptyNotes_OmitsNotesBlock()
    {
        var content = EmailTemplates.ServiceRequestCreated(EmailTemplates.DefaultCulture, "S", "cleaning", "Villa", "  ", Link);

        Assert.DoesNotContain("Note:", content.HtmlBody);
    }

    [Fact]
    public void Quote_MultilineNotes_KeepsLineBreaksAndEncodesEachLine()
    {
        var content = EmailTemplates.ServiceRequestCreated(
            EmailTemplates.DefaultCulture, "S", "cleaning", "Villa", "riga 1\r\n<b>riga 2</b>", Link);

        Assert.Contains("riga 1<br />&lt;b&gt;riga 2&lt;/b&gt;", content.HtmlBody);
    }

    [Fact]
    public void Subject_ValueWithLineBreaks_IsSingleLine()
    {
        var content = EmailTemplates.ServiceRequestCreated(
            EmailTemplates.DefaultCulture, "S", "cleaning", "Villa\r\nBcc: attacker@example.com", null, Link);

        Assert.DoesNotContain('\n', content.Subject);
        Assert.DoesNotContain('\r', content.Subject);
    }

    [Fact]
    public void SupplierInvite_ExpiryUtc_IsShownInItalianTime()
    {
        var content = EmailTemplates.SupplierInvite(
            EmailTemplates.DefaultCulture,
            "fornitore@example.com",
            "H501",
            null,
            "https://casazen-app.test/register?inviteToken=1&email=fornitore%40example.com&comune=H501",
            new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("L'invito scade il <strong>27/06/2026 14:00</strong>.", content.HtmlBody);
        Assert.Contains("href=\"https://casazen-app.test/register?inviteToken=1&amp;email=fornitore%40example.com&amp;comune=H501\"", content.HtmlBody);
    }

    [Fact]
    public void GuestCheckInLink_English_FormatsStayDateUnambiguously()
    {
        var content = EmailTemplates.GuestCheckInLink(
            CultureInfo.GetCultureInfo("en"), "Anna", "Villa Rosa", CheckIn, "https://casazen-app.test/checkin/abc");

        Assert.Equal("Complete the check-in for your stay — Villa Rosa", content.Subject);
        Assert.Contains("starts on <strong>5 October 2026</strong>", content.HtmlBody);
    }

    [Fact]
    public void AlloggiatiDeadline_Italian_ContainsGuestPropertyAndDate()
    {
        var content = EmailTemplates.AlloggiatiDeadline(EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn);

        Assert.Equal("Alloggiati Web in scadenza - Villa Rosa (05/10/2026)", content.Subject);
        Assert.Contains("<strong>Anna</strong>", content.HtmlBody);
        Assert.Contains("<strong>05/10/2026</strong>", content.HtmlBody);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/app/supplier/inbox")]
    public void Button_NonHttpUrl_IsRejected(string url)
    {
        Assert.Throws<ArgumentException>(() =>
            EmailTemplates.ServiceRequestCreated(EmailTemplates.DefaultCulture, "S", "cleaning", "Villa", null, url));
    }

    [Fact]
    public void ServiceRequestStatusChanged_StatusWithoutEmail_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EmailTemplates.ServiceRequestStatusChanged(
                EmailTemplates.DefaultCulture, ServiceRequestStatus.Pagato, "cleaning", "Villa"));
    }

    [Fact]
    public void Resources_EnglishFile_HasSameKeysAsItalianWithValues()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.NotEmpty(italian);
        Assert.Empty(italian.Keys.Except(english.Keys));
        Assert.Empty(english.Keys.Except(italian.Keys));
        Assert.All(italian.Concat(english), entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key));
        var untranslated = italian.Where(entry => english[entry.Key] == entry.Value).Select(entry => entry.Key).ToList();
        Assert.True(untranslated.Count == 0, $"Same text in Italian and English: {string.Join(", ", untranslated)}");
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var set = EmailTexts.Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);
    }

    private static EmailContent Render(string template, string cultureName, string value)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        return template switch
        {
            "created" => EmailTemplates.ServiceRequestCreated(culture, value, value, value, value, Link),
            "taken" => EmailTemplates.ServiceRequestStatusChanged(culture, ServiceRequestStatus.PresoInCarico, value, value),
            "completed" => EmailTemplates.ServiceRequestStatusChanged(culture, ServiceRequestStatus.Completato, value, value),
            "rejected" => EmailTemplates.ServiceRequestStatusChanged(culture, ServiceRequestStatus.Rifiutato, value, value, value),
            "invite" => EmailTemplates.SupplierInvite(culture, value, value, value, Link, DateTime.UtcNow),
            "checkin-link" => EmailTemplates.GuestCheckInLink(culture, value, value, CheckIn, Link),
            "checkin-incomplete" => EmailTemplates.GuestCheckInIncomplete(culture, value, value, CheckIn),
            "alloggiati" => EmailTemplates.AlloggiatiDeadline(culture, value, value, CheckIn),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };
    }
}
