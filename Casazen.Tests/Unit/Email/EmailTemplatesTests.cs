using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
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
        "created", "taken", "completed", "rejected", "invite", "checkin-link", "checkin-incomplete", "alloggiati", "refund",
        "late-payment-confirmed", "late-payment-refunded", "rli-reminder", "rli-overdue", "rli-extra-eu", "onsite-received",
        "onsite-to-host", "onsite-accepted", "onsite-declined", "onsite-expired", "booking-cancelled",
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
            "https://casazen-app.test/register?inviteToken=1",
            new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("L'invito scade il <strong>27/06/2026 14:00</strong> (ora italiana).", content.HtmlBody);
        Assert.Contains("href=\"https://casazen-app.test/register?inviteToken=1\"", content.HtmlBody);
    }

    [Fact]
    public void SupplierInvite_WinterExpiry_IsShownInItalianTimeInBothLanguages()
    {
        var expiresAt = new DateTime(2026, 12, 1, 23, 30, 0, DateTimeKind.Utc);

        var italian = EmailTemplates.SupplierInvite(
            EmailTemplates.DefaultCulture, "fornitore@example.com", "Roma (H501)", null, Link, expiresAt);
        var english = EmailTemplates.SupplierInvite(
            CultureInfo.GetCultureInfo("en"), "fornitore@example.com", "Roma (H501)", null, Link, expiresAt);

        // 23:30 UTC on 1 December is 00:30 of 2 December in Rome (CET, UTC+1).
        Assert.Contains("<strong>02/12/2026 00:30</strong> (ora italiana)", italian.HtmlBody);
        Assert.Contains("(Italian time)", english.HtmlBody);
    }

    [Fact]
    public void SupplierInvite_Text_PromisesTheActivationWizardNotAnAutomaticActivation()
    {
        var italian = EmailTemplates.SupplierInvite(
            EmailTemplates.DefaultCulture, "fornitore@example.com", "Roma (H501)", null, Link, DateTime.UtcNow);
        var english = EmailTemplates.SupplierInvite(
            CultureInfo.GetCultureInfo("en"), "fornitore@example.com", "Roma (H501)", null, Link, DateTime.UtcNow);

        Assert.DoesNotContain("automaticamente", italian.HtmlBody);
        Assert.DoesNotContain("automatically", english.HtmlBody);
        Assert.Contains("procedura di attivazione", italian.HtmlBody);
        Assert.Contains("activation steps", english.HtmlBody);
        Assert.Contains("per il comune <strong>Roma (H501)</strong>", italian.HtmlBody);
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

    [Fact]
    public void GuestRefundConfirmed_ItalianAndEnglish_ShowAmountInEuroPropertyAndArrival()
    {
        var italian = EmailTemplates.GuestRefundConfirmed(CultureInfo.GetCultureInfo("it-IT"), "Anna", "Villa Rosa", CheckIn, 1234.5m);
        var english = EmailTemplates.GuestRefundConfirmed(CultureInfo.GetCultureInfo("en"), "Anna", "Villa Rosa", CheckIn, 1234.5m);

        Assert.Equal("Rimborso confermato - Villa Rosa", italian.Subject);
        Assert.Contains("1.234,50 €", italian.HtmlBody);
        Assert.Contains("Villa Rosa", italian.HtmlBody);
        Assert.Contains("€1,234.50", english.HtmlBody);
        Assert.Equal("Refund confirmed - Villa Rosa", english.Subject);
    }

    [Fact]
    public void OnSiteRequestReceived_Italian_SaysNotConfirmedYetAndGivesLinkAndDeadlineInItalianTime()
    {
        const string confirmUrl = "https://casazen-app.test/book/villa/requests/1/confirm?token=abc";
        var content = EmailTemplates.OnSiteRequestReceived(
            EmailTemplates.DefaultCulture,
            "Ada",
            "Villa Rosa",
            CheckIn,
            CheckIn.AddDays(3),
            1234.5m,
            confirmUrl,
            new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Conferma la tua richiesta di prenotazione - Villa Rosa", content.Subject);
        Assert.Contains("dal <strong>05/10/2026</strong> al <strong>08/10/2026</strong>", content.HtmlBody);
        Assert.Contains("<strong>1.234,50 €</strong> in struttura", content.HtmlBody);
        Assert.Contains("entro il <strong>27/06/2026 14:00</strong> (ora italiana)", content.HtmlBody);
        Assert.Contains($"href=\"{confirmUrl.Replace("&", "&amp;", StringComparison.Ordinal)}\"", content.HtmlBody);
        Assert.Contains("non è ancora confermata", content.HtmlBody);
    }

    [Fact]
    public void OnSiteRequestToHost_English_AsksToAnswerByDeadlineAndLinksTheConsole()
    {
        var content = EmailTemplates.OnSiteRequestToHost(
            CultureInfo.GetCultureInfo("en"),
            "Ada Lovelace",
            "Villa Rosa",
            CheckIn,
            CheckIn.AddDays(3),
            2,
            450m,
            new DateTime(2026, 12, 1, 23, 30, 0, DateTimeKind.Utc),
            Link);

        Assert.Equal("New booking request to approve - Villa Rosa", content.Subject);
        Assert.Contains("<strong>Ada Lovelace</strong> asks to book <strong>Villa Rosa</strong>", content.HtmlBody);
        Assert.Contains("by <strong>2 December 2026, 00:30</strong> (Italian time)", content.HtmlBody);
        Assert.Contains($"href=\"{Link}\"", content.HtmlBody);
    }

    [Fact]
    public void GuestBookingCancelled_ItalianAndEnglish_ShowStayAndRefundOnlyWhenStarted()
    {
        var italian = EmailTemplates.GuestBookingCancelled(
            CultureInfo.GetCultureInfo("it-IT"), "Anna", "Villa Rosa", CheckIn, CheckIn.AddDays(3), 1234.5m);
        var english = EmailTemplates.GuestBookingCancelled(
            CultureInfo.GetCultureInfo("en"), "Anna", "Villa Rosa", CheckIn, CheckIn.AddDays(3), 1234.5m);
        var withoutRefund = EmailTemplates.GuestBookingCancelled(
            CultureInfo.GetCultureInfo("it-IT"), "Anna", "Villa Rosa", CheckIn, CheckIn.AddDays(3), null);

        Assert.Equal("Prenotazione annullata - Villa Rosa", italian.Subject);
        Assert.Contains("1.234,50 €", italian.HtmlBody);
        Assert.Contains("05/10/2026", italian.HtmlBody);
        Assert.Contains("08/10/2026", italian.HtmlBody);
        Assert.Equal("Booking cancelled - Villa Rosa", english.Subject);
        Assert.Contains("€1,234.50", english.HtmlBody);
        Assert.DoesNotContain("rimborso", withoutRefund.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RliDeadlineReminder_Italian_ShowsPropertyDeadlineAndDaysWithoutTechnicalCodes()
    {
        var content = EmailTemplates.RliDeadlineReminder(EmailTemplates.DefaultCulture, "Villa Rosa", CheckIn, 7);

        Assert.Equal("Promemoria registrazione RLI — Villa Rosa (scadenza 05/10/2026)", content.Subject);
        Assert.Contains("<strong>Villa Rosa</strong>", content.HtmlBody);
        Assert.Contains("<strong>05/10/2026</strong>", content.HtmlBody);
        Assert.Contains("Giorni rimanenti: <strong>7</strong>", content.HtmlBody);
        Assert.DoesNotContain("t-7", content.HtmlBody + content.Subject);
    }

    [Fact]
    public void RliExtraEuNotice_EnglishAndItalian_MentionQuestura()
    {
        var italian = EmailTemplates.RliExtraEuNotice(EmailTemplates.DefaultCulture, "Villa Rosa");
        var english = EmailTemplates.RliExtraEuNotice(CultureInfo.GetCultureInfo("en"), "Villa Rosa");

        Assert.Contains("Questura", italian.Subject);
        Assert.Contains("Questura", english.Subject);
        Assert.Contains("conduttore extra-UE", italian.HtmlBody);
        Assert.Contains("non-EU tenant", english.HtmlBody);
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

    [Fact]
    public void GuestLatePaymentConfirmed_Italian_SaysTheDatesWereFreeAndGivesTheBookingCode()
    {
        var content = EmailTemplates.GuestLatePaymentConfirmed(
            EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn, CheckIn.AddDays(3), "b7d3c1f0-0000-4000-8000-000000000001");

        Assert.Equal("Prenotazione confermata - Villa Rosa", content.Subject);
        Assert.Contains("Gentile Anna,", content.HtmlBody);
        Assert.Contains("dal <strong>05/10/2026</strong> al <strong>08/10/2026</strong>", content.HtmlBody);
        Assert.Contains("le date erano ancora libere: la prenotazione è confermata", content.HtmlBody);
        Assert.Contains("Codice prenotazione: b7d3c1f0-0000-4000-8000-000000000001", content.HtmlBody);
    }

    [Fact]
    public void GuestPaymentRefundedDatesUnavailable_Italian_ExplainsWhyAndGivesTheFullAmount()
    {
        var content = EmailTemplates.GuestPaymentRefundedDatesUnavailable(
            EmailTemplates.DefaultCulture, "Anna", "Villa Rosa", CheckIn, CheckIn.AddDays(3), 1234.5m);

        Assert.Equal("Date non più disponibili, pagamento rimborsato - Villa Rosa", content.Subject);
        Assert.Contains("le date non erano più disponibili", content.HtmlBody);
        Assert.Contains("Non è stato possibile confermare la prenotazione.", content.HtmlBody);
        Assert.Contains("l'intero importo pagato, <strong>1.234,50 €</strong>", content.HtmlBody);
        Assert.Contains("L'importo torna sul metodo di pagamento usato per la prenotazione.", content.HtmlBody);
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
            "refund" => EmailTemplates.GuestRefundConfirmed(culture, value, value, CheckIn, 1234.5m),
            "late-payment-confirmed" => EmailTemplates.GuestLatePaymentConfirmed(culture, value, value, CheckIn, CheckIn.AddDays(3), value),
            "late-payment-refunded" => EmailTemplates.GuestPaymentRefundedDatesUnavailable(
                culture, value, value, CheckIn, CheckIn.AddDays(3), 1234.5m),
            "rli-reminder" => EmailTemplates.RliDeadlineReminder(culture, value, CheckIn, 7),
            "rli-overdue" => EmailTemplates.RliDeadlineOverdue(culture, value, CheckIn),
            "rli-extra-eu" => EmailTemplates.RliExtraEuNotice(culture, value),
            "booking-cancelled" => EmailTemplates.GuestBookingCancelled(culture, value, value, CheckIn, CheckIn.AddDays(3), 99.5m),
            "onsite-received" => EmailTemplates.OnSiteRequestReceived(
                culture, value, value, CheckIn, CheckIn.AddDays(3), 450m, Link, DateTime.UtcNow),
            "onsite-to-host" => EmailTemplates.OnSiteRequestToHost(
                culture, value, value, CheckIn, CheckIn.AddDays(3), 2, 450m, DateTime.UtcNow, Link),
            "onsite-accepted" => EmailTemplates.OnSiteRequestAccepted(culture, value, value, CheckIn, CheckIn.AddDays(3), 450m, value),
            "onsite-declined" => EmailTemplates.OnSiteRequestDeclined(culture, value, value, CheckIn, CheckIn.AddDays(3), value),
            "onsite-expired" => EmailTemplates.OnSiteRequestExpired(culture, value, value, CheckIn, CheckIn.AddDays(3)),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };
    }
}
