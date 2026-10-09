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

        // SP-11: the customer's own area of a booking.
        "cancellation-receipt", "cancellation-receipt-no-reason", "proposal-expired", "cancelled-by-customer", "cancelled-by-customer-bare",
        "rescheduled-by-customer", "rescheduled-by-customer-bare", "proposal-accepted-by-customer", "proposal-rejected-by-customer",
        "proposal-lapsed",
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
        "cancellation-receipt" => EmailTemplates.SupplierBookingCancellationReceipt(culture, value, value, value, Start, "ABCDE12345", value, Link),
        "cancellation-receipt-no-reason" => EmailTemplates.SupplierBookingCancellationReceipt(culture, value, value, value, Start, "ABCDE12345", null, Link),
        "proposal-expired" => EmailTemplates.SupplierBookingProposalExpired(culture, value, value, value, Start, Link),
        "cancelled-by-customer" => EmailTemplates.SupplierBookingCancelledByCustomer(culture, value, value, value, value, Start, value, 24, Link),
        "cancelled-by-customer-bare" => EmailTemplates.SupplierBookingCancelledByCustomer(culture, value, value, value, value, Start, null, null, Link),
        "rescheduled-by-customer" => EmailTemplates.SupplierBookingRescheduledByCustomer(culture, value, value, value, value, Start, Start.AddDays(1), AnswerBy, true, Link),
        "rescheduled-by-customer-bare" => EmailTemplates.SupplierBookingRescheduledByCustomer(culture, value, value, value, value, Start, Start.AddDays(1), AnswerBy, false, Link),
        "proposal-accepted-by-customer" => EmailTemplates.SupplierBookingProposalAnsweredByCustomer(culture, value, value, value, value, true, Start, null, Link),
        "proposal-rejected-by-customer" => EmailTemplates.SupplierBookingProposalAnsweredByCustomer(culture, value, value, value, value, false, Start, AnswerBy, Link),
        "proposal-lapsed" => EmailTemplates.SupplierBookingProposalLapsed(culture, value, value, value, value, Link),
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
            EmailTemplates.Names.SupplierBookingCancellationReceipt,
            EmailTemplates.Names.SupplierBookingProposalExpired,
            EmailTemplates.Names.SupplierBookingCancelledByCustomer,
            EmailTemplates.Names.SupplierBookingRescheduledByCustomer,
            EmailTemplates.Names.SupplierBookingProposalAnsweredByCustomer,
            EmailTemplates.Names.SupplierBookingProposalLapsed,
        };

        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, name => Assert.Matches("^supplier-booking-[a-z-]+$", name));
    }

    // ─── SP-11: the customer's own area of a booking ───

    [Fact]
    public void CancellationReceipt_GivesTheCodeAndTheReasonTheCustomerWrote_AndSaysNothingIsChargedForCancelling()
    {
        var content = EmailTemplates.SupplierBookingCancellationReceipt(
            Italian, "Mario Rossi", "Pulizie Rossi", "Pulizia appartamento", Start, "ABCDE12345", "Cambio programma", Link);
        var withoutReason = EmailTemplates.SupplierBookingCancellationReceipt(
            Italian, "Mario Rossi", "Pulizie Rossi", "Pulizia appartamento", Start, "ABCDE12345", null, Link);

        Assert.Equal("Hai annullato la richiesta a Pulizie Rossi", content.Subject);
        Assert.Contains("Ciao Mario Rossi,", content.HtmlBody);
        Assert.Contains("Hai annullato la richiesta di <strong>Pulizia appartamento</strong> a <strong>Pulizie Rossi</strong> per <strong>09/10/2026 10:00</strong>.", content.HtmlBody);
        Assert.Contains("Codice della richiesta: <strong>ABCDE-12345</strong>.", content.HtmlBody);
        Assert.Contains("Il motivo che hai indicato:", content.HtmlBody);
        Assert.Contains("Cambio programma", content.HtmlBody);
        // Decision D6: no exit cost in v1, and the e-mail may say so; nothing else about money.
        Assert.Contains("Non hai pagato nulla e non ti addebitiamo nulla per l'annullo.", content.HtmlBody);
        Assert.DoesNotContain("Il motivo che hai indicato", withoutReason.HtmlBody);
        Assert.DoesNotContain("penale", content.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProposalExpired_SaysItIsTheCustomerWhoDidNotAnswer_NotTheSupplier()
    {
        var content = EmailTemplates.SupplierBookingProposalExpired(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, Link);
        var expired = EmailTemplates.SupplierBookingExpired(Italian, "Mario", "Pulizie Rossi", "Pulizia", Start, Link);

        Assert.Equal("Richiesta a Pulizie Rossi annullata: nuovo orario senza risposta", content.Subject);
        Assert.Contains("ti aveva proposto un altro orario", content.HtmlBody);
        Assert.Contains("ma non hai risposto in tempo", content.HtmlBody);
        Assert.Contains("Non hai pagato nulla.", content.HtmlBody);
        Assert.DoesNotContain("non ha risposto", content.HtmlBody);
        // And the e-mail of the request nobody answered still blames nobody but the supplier.
        Assert.Contains("non ha risposto in tempo", expired.HtmlBody);
    }

    [Fact]
    public void CancelledByCustomer_TheSupplierReadsTheComuneTheShortNameAndTheReason_AndTheNoticeOnlyWhenItWasShort()
    {
        var short1 = EmailTemplates.SupplierBookingCancelledByCustomer(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", Start, "Cambio programma", 24, Link);
        var free = EmailTemplates.SupplierBookingCancelledByCustomer(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", Start, null, null, Link);
        var unnamed = EmailTemplates.SupplierBookingCancelledByCustomer(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "", Start, null, null, Link);

        Assert.Equal("Richiesta annullata dal cliente — Monza", short1.Subject);
        Assert.Contains("Una richiesta di <strong>Pulizia appartamento</strong> a <strong>Monza</strong> per <strong>09/10/2026 10:00</strong> è stata <strong>annullata dal cliente</strong>.", short1.HtmlBody);
        Assert.Contains("Cliente: <strong>Mario R.</strong>.", short1.HtmlBody);
        Assert.Contains("Cambio programma", short1.HtmlBody);
        Assert.Contains("L'annullo è arrivato con meno di <strong>24 h</strong> di preavviso.", short1.HtmlBody);
        Assert.DoesNotContain("preavviso", free.HtmlBody);
        Assert.DoesNotContain("Il motivo:", free.HtmlBody);
        Assert.DoesNotContain("Cliente:", unnamed.HtmlBody);
    }

    [Fact]
    public void RescheduledByCustomer_NamesBothTimes_TheNewDeadline_AndTheDroppedProposalOnlyWhenThereWasOne()
    {
        var withProposal = EmailTemplates.SupplierBookingRescheduledByCustomer(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", Start, Start.AddDays(1), AnswerBy, true, Link);
        var plain = EmailTemplates.SupplierBookingRescheduledByCustomer(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", Start, Start.AddDays(1), AnswerBy, false, Link);

        Assert.Equal("Il cliente ha cambiato orario — Monza", plain.Subject);
        Assert.Contains("da <strong>09/10/2026 10:00</strong> a <strong>10/10/2026 10:00</strong>", plain.HtmlBody);
        Assert.Contains("La richiesta è di nuovo in attesa: rispondi entro le <strong>08/10/2026 14:00</strong>, dopo quell'ora si annulla da sola.", plain.HtmlBody);
        Assert.Contains("L'orario che avevi proposto non vale più.", withProposal.HtmlBody);
        Assert.DoesNotContain("L'orario che avevi proposto", plain.HtmlBody);
    }

    [Fact]
    public void ProposalAnsweredByCustomer_AcceptedTakesTheRequest_TurnedDownLeavesItWaitingForTheSupplier()
    {
        var accepted = EmailTemplates.SupplierBookingProposalAnsweredByCustomer(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", true, Start.AddHours(4), null, Link);
        var rejected = EmailTemplates.SupplierBookingProposalAnsweredByCustomer(
            Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", false, Start, AnswerBy, Link);

        Assert.Equal("Il cliente ha accettato il nuovo orario — Monza", accepted.Subject);
        Assert.Contains("<strong>09/10/2026 14:00</strong>. La richiesta è ora presa in carico.", accepted.HtmlBody);
        Assert.Equal("Il cliente ha rifiutato il nuovo orario — Monza", rejected.Subject);
        Assert.Contains("La richiesta resta in attesa dell'orario richiesto, <strong>09/10/2026 10:00</strong>", rejected.HtmlBody);
        Assert.Contains("rispondi entro le <strong>08/10/2026 14:00</strong>", rejected.HtmlBody);
        Assert.Contains("Puoi prenderla in carico così com'è, proporre un altro orario o rifiutarla.", rejected.HtmlBody);
        Assert.DoesNotContain("La richiesta è ora presa in carico", rejected.HtmlBody);
    }

    [Fact]
    public void ProposalLapsed_TellsTheSupplierItIsTheCustomerWhoDidNotAnswer()
    {
        var content = EmailTemplates.SupplierBookingProposalLapsed(Italian, "Pulizie Rossi", "Pulizia appartamento", "Monza", "Mario R.", Link);

        Assert.Equal("Richiesta annullata: il cliente non ha risposto — Monza", content.Subject);
        Assert.Contains("Il cliente non ha risposto in tempo al nuovo orario che avevi proposto", content.HtmlBody);
        Assert.Contains("<strong>annullata</strong>", content.HtmlBody);
        Assert.DoesNotContain("non hai risposto", content.HtmlBody);
    }

    [Fact]
    public void English_TheNewMailsAreComplete_AndTheTimesAreStillInRomeTime()
    {
        var receipt = EmailTemplates.SupplierBookingCancellationReceipt(
            English, "Mario Rossi", "Pulizie Rossi", "Cleaning", Start, "ABCDE12345", "Change of plans", Link);
        var rescheduled = EmailTemplates.SupplierBookingRescheduledByCustomer(
            English, "Pulizie Rossi", "Cleaning", "Monza", "Mario R.", Start, Start.AddDays(1), AnswerBy, false, Link);

        Assert.Equal("You cancelled your request to Pulizie Rossi", receipt.Subject);
        Assert.Contains("9 October 2026, 10:00", receipt.HtmlBody);
        Assert.Contains("The reason you gave:", receipt.HtmlBody);
        Assert.Equal("The customer changed the time — Monza", rescheduled.Subject);
        Assert.Contains("from <strong>9 October 2026, 10:00</strong> to <strong>10 October 2026, 10:00</strong>", rescheduled.HtmlBody);
    }

    [Fact]
    public void Pushes_OfTheCustomersActions_AreShortAndNameOnlyTheCategoryAndTheComune()
    {
        var it = CultureInfo.GetCultureInfo("it-IT");

        var cancelled = EmailTemplates.ShowcaseCancelledByCustomerPush(it, "cleaning", "Monza");
        var rescheduled = EmailTemplates.ShowcaseRescheduledByCustomerPush(it, "cleaning", "Monza");
        var accepted = EmailTemplates.ShowcaseProposalAnsweredPush(it, "cleaning", "Monza", accepted: true);
        var rejected = EmailTemplates.ShowcaseProposalAnsweredPush(it, "cleaning", "Monza", accepted: false);
        var lapsed = EmailTemplates.ShowcaseProposalLapsedPush(it, "cleaning", "Monza");

        Assert.Equal(("Richiesta annullata dal cliente", "Pulizie a Monza: il cliente ha annullato la richiesta."), (cancelled.Title, cancelled.Body));
        Assert.Equal(("Il cliente ha cambiato orario", "Pulizie a Monza: la richiesta aspetta la tua risposta per il nuovo orario."), (rescheduled.Title, rescheduled.Body));
        Assert.Equal(("Nuovo orario accettato", "Pulizie a Monza: il cliente ha accettato il tuo orario."), (accepted.Title, accepted.Body));
        Assert.Equal(("Nuovo orario rifiutato", "Pulizie a Monza: il cliente ha rifiutato il tuo orario, la richiesta resta in attesa."), (rejected.Title, rejected.Body));
        Assert.Equal(("Richiesta annullata", "Pulizie a Monza: il cliente non ha risposto in tempo al nuovo orario."), (lapsed.Title, lapsed.Body));
        var english = EmailTemplates.ShowcaseCancelledByCustomerPush(CultureInfo.GetCultureInfo("en"), "cleaning", "Monza");
        Assert.Equal(("Request cancelled by the customer", "Cleaning in Monza: the customer cancelled the request."), (english.Title, english.Body));
    }
}
