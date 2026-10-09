using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// SP-10: the e-mails of the booking from a supplier's public showcase. Every dynamic value is HTML-encoded, both languages are
/// complete, the supplier is told "Nome C." and never more (decision D9), and nothing is promised that the jobs do not enforce
/// (decision D24: no «miglior prezzo garantito», no «risponde in un'ora», no reminder two days before).
/// </summary>
public class SupplierBookingEmailTemplatesTests
{
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";
    private const string Link = "https://casazen-app.test/fornitori/vetrina-test/richiesta?code=ABCDE-12345";

    /// <summary>Friday 9 October 2026, 10:00 in Rome.</summary>
    private static readonly DateTime Start = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime AnswerBy = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static TheoryData<string> TemplateNames => new()
    {
        "verification", "receipt", "receipt-no-price", "new-request", "new-request-no-price", "accepted-quoted", "accepted-estimated",
        "accepted-no-reminder", "declined", "time-proposed", "reminder", "cancelled", "expired",
    };

    private static EmailContent Render(string template, CultureInfo culture, string value) => template switch
    {
        "verification" => EmailTemplates.SupplierBookingVerification(culture, value, value, value, Start, 30, Link),
        "receipt" => EmailTemplates.SupplierBookingReceipt(culture, value, value, value, Start, value, AnswerBy, 6000, Link),
        "receipt-no-price" => EmailTemplates.SupplierBookingReceipt(culture, value, value, value, Start, value, AnswerBy, null, Link),
        "new-request" => EmailTemplates.SupplierBookingNewRequest(culture, value, value, value, value, Start, 6000, AnswerBy, Link),
        "new-request-no-price" => EmailTemplates.SupplierBookingNewRequest(culture, value, value, value, value, Start, null, AnswerBy, Link),
        "accepted-quoted" => EmailTemplates.SupplierBookingAccepted(culture, value, value, value, Start, 7000, 6000, true, Link),
        "accepted-estimated" => EmailTemplates.SupplierBookingAccepted(culture, value, value, value, Start, null, 6000, true, Link),
        "accepted-no-reminder" => EmailTemplates.SupplierBookingAccepted(culture, value, value, value, Start, null, null, false, Link),
        "declined" => EmailTemplates.SupplierBookingDeclined(culture, value, value, value, Start, value, Link),
        "time-proposed" => EmailTemplates.SupplierBookingTimeProposed(culture, value, value, value, Start, Start.AddHours(2), value, AnswerBy, Link),
        "reminder" => EmailTemplates.SupplierBookingReminder(culture, value, value, value, Start, Link),
        "cancelled" => EmailTemplates.SupplierBookingCancelled(culture, value, value, value, Start, value, Link),
        "expired" => EmailTemplates.SupplierBookingExpired(culture, value, value, value, Start, Link),
        _ => throw new ArgumentOutOfRangeException(nameof(template)),
    };

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public void Render_PayloadInEveryDynamicValue_IsHtmlEncoded(string template)
    {
        var content = Render(template, Italian, Payload);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.DoesNotContain("phish.example\">", content.HtmlBody);
        Assert.Contains(EncodedPayload, content.HtmlBody);
    }

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public void Render_ItalianAndEnglish_ProduceCompleteLocalizedDocuments(string template)
    {
        var italian = Render(template, Italian, "Pulizie Rossi");
        var english = Render(template, English, "Pulizie Rossi");

        Assert.StartsWith("<!DOCTYPE html>", italian.HtmlBody);
        Assert.Contains("<html lang=\"it\">", italian.HtmlBody);
        Assert.Contains("<html lang=\"en\">", english.HtmlBody);
        Assert.NotEqual(italian.Subject, english.Subject);
        foreach (var content in new[] { italian, english })
        {
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
            Assert.False(string.IsNullOrWhiteSpace(content.Subject));
            Assert.DoesNotContain('\n', content.Subject);
            Assert.Contains($"href=\"{Link}\"", content.HtmlBody);
        }
    }

    [Theory]
    [MemberData(nameof(TemplateNames))]
    public void Render_NothingIsPromisedThatTheJobsDoNotEnforce(string template)
    {
        foreach (var culture in new[] { Italian, English })
        {
            var text = Regex.Replace(Render(template, culture, "Pulizie Rossi").HtmlBody, "<[^>]+>", " ").ToLowerInvariant();

            foreach (var banned in new[]
                     {
                         "miglior prezzo", "best price", "garantit", "guarantee", "un'ora", "within an hour", "in an hour",
                         "due giorni", "2 giorni", "two days", "48 ore", "48 hours", "pagamento sicuro", "secure payment",
                     })
            {
                Assert.DoesNotContain(banned, text);
            }
        }
    }

    [Fact]
    public void Verification_TheCustomerHearsThatTheSupplierReceivesNothingUntilTheLinkIsFollowed()
    {
        var content = EmailTemplates.SupplierBookingVerification(Italian, "Mario Rossi", "Pulizie Rossi", "Pulizia appartamento", Start, 30, Link);

        Assert.Equal("Conferma la tua richiesta a Pulizie Rossi", content.Subject);
        Assert.Contains("Ciao Mario Rossi,", content.HtmlBody);
        Assert.Contains("<strong>Pulizia appartamento</strong>", content.HtmlBody);
        Assert.Contains("<strong>09/10/2026 10:00</strong>", content.HtmlBody);
        Assert.Contains("il fornitore non riceve nulla", content.HtmlBody);
        Assert.Contains("Teniamo libero l'orario per 30 minuti", content.HtmlBody);
        Assert.Contains("Se non hai chiesto tu questa prenotazione, ignora questo messaggio", content.HtmlBody);
        // The button and the plain link for the clients that do not draw buttons.
        Assert.Equal(2, Regex.Matches(content.HtmlBody, Regex.Escape($"href=\"{Link}\"")).Count);
    }

    [Fact]
    public void Receipt_GivesTheCodeTheDeadlineAndTheEstimate_AndSaysNothingIsPaid()
    {
        var content = EmailTemplates.SupplierBookingReceipt(
            Italian, "Mario Rossi", "Pulizie Rossi", "Pulizia appartamento", Start, "ABCDE12345", AnswerBy, 6000, Link);

        Assert.Equal("Richiesta inviata a Pulizie Rossi", content.Subject);
        Assert.Contains("<strong>ABCDE-12345</strong>", content.HtmlBody);
        Assert.Contains("Se non risponde entro le <strong>08/10/2026 14:00</strong>, la richiesta si annulla", content.HtmlBody);
        Assert.Contains("Prezzo stimato: <strong>60,00 €</strong>. È una stima", content.HtmlBody);
        Assert.Contains("Con questa richiesta non paghi nulla adesso.", content.HtmlBody);
    }

    [Fact]
    public void Receipt_ServiceOnQuote_HasNoPriceLine()
    {
        var content = EmailTemplates.SupplierBookingReceipt(
            Italian, "Mario Rossi", "Pulizie Rossi", "Pulizia appartamento", Start, "ABCDE12345", AnswerBy, null, Link);

        Assert.DoesNotContain("Prezzo stimato", content.HtmlBody);
        Assert.DoesNotContain("€", content.HtmlBody);
    }

    [Fact]
    public void NewRequest_TheSupplierReadsTheComuneAndTheShortNameOnly()
    {
        var content = EmailTemplates.SupplierBookingNewRequest(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", Start, 6000, AnswerBy, Link);

        Assert.Equal("Nuova richiesta dal tuo sito — Monza", content.Subject);
        Assert.Contains("Cliente: <strong>Mario R.</strong>.", content.HtmlBody);
        Assert.Contains("a <strong>Monza</strong>", content.HtmlBody);
        Assert.Contains("Importo stimato: <strong>60,00 €</strong>.", content.HtmlBody);
        Assert.Contains("Rispondi entro le <strong>08/10/2026 14:00</strong>", content.HtmlBody);
        Assert.Contains("dopo che accetti la richiesta", content.HtmlBody);
        Assert.DoesNotContain("Rossi</strong>", content.HtmlBody);
    }

    [Fact]
    public void NewRequest_ServiceOnQuote_HasNoAmountLine()
    {
        var content = EmailTemplates.SupplierBookingNewRequest(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", Start, null, AnswerBy, Link);

        Assert.DoesNotContain("Importo stimato", content.HtmlBody);
    }

    [Fact]
    public void Accepted_TheSuppliersOwnPriceWinsOverTheEstimate_AndTheReminderIsNamedOnlyWhenItWillBeSent()
    {
        var quoted = EmailTemplates.SupplierBookingAccepted(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, 7000, 6000, true, Link);
        var estimated = EmailTemplates.SupplierBookingAccepted(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, null, 6000, true, Link);
        var neither = EmailTemplates.SupplierBookingAccepted(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, null, null, false, Link);

        Assert.Equal("Pulizie Rossi ha accettato la tua richiesta", quoted.Subject);
        Assert.Contains("Prezzo indicato dal fornitore: <strong>70,00 €</strong>.", quoted.HtmlBody);
        Assert.DoesNotContain("60,00", quoted.HtmlBody);
        Assert.Contains("Prezzo stimato: <strong>60,00 €</strong>.", estimated.HtmlBody);
        Assert.Contains("Ti mandiamo un promemoria il giorno prima, alle 18:00.", quoted.HtmlBody);
        Assert.DoesNotContain("€", neither.HtmlBody);
        Assert.DoesNotContain("promemoria", neither.HtmlBody);
    }

    [Fact]
    public void DeclinedAndCancelled_QuoteTheReasonOfTheSupplier_AndOmitItWhenThereIsNone()
    {
        var declined = EmailTemplates.SupplierBookingDeclined(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, "Siamo in ferie", Link);
        var cancelled = EmailTemplates.SupplierBookingCancelled(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, "Guasto al furgone", Link);
        var withoutReason = EmailTemplates.SupplierBookingDeclined(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, null, Link);

        Assert.Equal("Pulizie Rossi non può accettare la tua richiesta", declined.Subject);
        Assert.Contains("Siamo in ferie", declined.HtmlBody);
        Assert.Contains("Non hai pagato nulla.", declined.HtmlBody);
        Assert.Equal("La tua richiesta a Pulizie Rossi è stata annullata", cancelled.Subject);
        Assert.Contains("Guasto al furgone", cancelled.HtmlBody);
        Assert.DoesNotContain("Il motivo:", withoutReason.HtmlBody);
    }

    [Fact]
    public void TimeProposed_NamesBothTimesTheMessageAndTheDeadline()
    {
        var content = EmailTemplates.SupplierBookingTimeProposed(
            Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, Start.AddHours(2), "Posso solo nel pomeriggio", AnswerBy, Link);

        Assert.Equal("Pulizie Rossi propone un altro orario", content.Subject);
        Assert.Contains("<strong>09/10/2026 10:00</strong>", content.HtmlBody);
        Assert.Contains("<strong>09/10/2026 12:00</strong>", content.HtmlBody);
        Assert.Contains("Posso solo nel pomeriggio", content.HtmlBody);
        Assert.Contains("Rispondi entro le <strong>08/10/2026 14:00</strong>: se non lo fai, la richiesta si annulla.", content.HtmlBody);
    }

    [Fact]
    public void Reminder_IsForTomorrow_AndNamesTheServiceAndTheSupplier()
    {
        var content = EmailTemplates.SupplierBookingReminder(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, Link);

        Assert.Equal("Domani: Pulizia con Pulizie Rossi", content.Subject);
        Assert.Contains("Ti ricordiamo l'appuntamento di domani", content.HtmlBody);
        Assert.Contains("<strong>09/10/2026 10:00</strong>", content.HtmlBody);
    }

    [Fact]
    public void Expired_SaysTheSupplierDidNotAnswerInTime_AndThatNothingWasPaid()
    {
        var content = EmailTemplates.SupplierBookingExpired(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, Link);

        Assert.Equal("La tua richiesta a Pulizie Rossi è scaduta", content.Subject);
        Assert.Contains("non ha risposto in tempo", content.HtmlBody);
        Assert.Contains("Non hai pagato nulla.", content.HtmlBody);
    }

    [Fact]
    public void English_TimesAreStillInRomeTime_AndTheAmountInEuro()
    {
        var content = EmailTemplates.SupplierBookingReceipt(
            English, "Mario Rossi", "Pulizie Rossi", "Cleaning", Start, "ABCDE12345", AnswerBy, 6000, Link);

        Assert.Equal("Request sent to Pulizie Rossi", content.Subject);
        Assert.Contains("10:00", content.HtmlBody);
        Assert.Contains("60.00 €", content.HtmlBody);
        Assert.Contains("You pay nothing now with this request.", content.HtmlBody);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("it", "it")]
    [InlineData("de", "it")]
    [InlineData("", "it")]
    [InlineData(null, "it")]
    public void CultureOf_IsEnglishForEnglishAndItalianForEverythingElse(string? locale, string expected) =>
        Assert.Equal(expected, EmailTemplates.CultureOf(locale).TwoLetterISOLanguageName);

    [Fact]
    public void Names_AreOneForEachTemplate_AndKebabCase()
    {
        var names = new[]
        {
            EmailTemplates.Names.SupplierBookingVerification,
            EmailTemplates.Names.SupplierBookingReceipt,
            EmailTemplates.Names.SupplierBookingNewRequest,
            EmailTemplates.Names.SupplierBookingAccepted,
            EmailTemplates.Names.SupplierBookingDeclined,
            EmailTemplates.Names.SupplierBookingTimeProposed,
            EmailTemplates.Names.SupplierBookingReminder,
            EmailTemplates.Names.SupplierBookingCancelled,
            EmailTemplates.Names.SupplierBookingExpired,
        };

        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, name => Assert.Matches("^supplier-booking-[a-z-]+$", name));
    }
}
