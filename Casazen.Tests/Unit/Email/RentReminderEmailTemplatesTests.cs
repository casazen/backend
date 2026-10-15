using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// LR-01, B1: the reminder of an unpaid rent that the landlord sends to a tenant, in Italian and English, with every dynamic value
/// HTML-encoded (FD-13): the note, the property, the names, the landlord. With or without the personal payment link.
/// </summary>
public class RentReminderEmailTemplatesTests
{
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";
    private const string PayUrl = "https://casazen-app.test/rent/pay/6f1f8a0e-2b9c-4c55-9c1c-2f6a3a1d2c11?token=abcDEF123_-";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly DateOnly PeriodStart = new(2026, 10, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 10, 31);
    private static readonly DateOnly Due = new(2026, 10, 5);

    private static EmailContent Render(
        CultureInfo culture,
        bool overdue = true,
        string? note = null,
        string? payUrl = null,
        string tenant = "Giulia Verdi",
        string property = "Bilocale Sparano",
        string landlord = "Casa Rossi Srl") =>
        EmailTemplates.RentReminder(culture, tenant, property, landlord, PeriodStart, PeriodEnd, Due, 1234.5m, overdue, note, payUrl);

    [Fact]
    public void Italian_Overdue_SaysItWasDueAndNotPaid_WithAmountPropertyPeriodAndDates()
    {
        var content = Render(EmailTemplates.DefaultCulture);

        Assert.Equal("Promemoria canone di locazione - Bilocale Sparano, scadenza 05/10/2026", content.Subject);
        Assert.Contains("Gentile Giulia Verdi,", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(
            "ti ricordiamo che il canone di <strong>1.234,50 €</strong> per l'immobile <strong>Bilocale Sparano</strong>, periodo dal <strong>01/10/2026</strong> al <strong>31/10/2026</strong>, era in scadenza il <strong>05/10/2026</strong> e non risulta ancora pagato.",
            content.HtmlBody,
            StringComparison.Ordinal);
        Assert.Contains("<html lang=\"it\">", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Italian_NotDueYet_SaysWhenItIsDueNotThatItWasMissed()
    {
        var content = Render(EmailTemplates.DefaultCulture, overdue: false);

        Assert.Contains("è da pagare entro il <strong>05/10/2026</strong> e non risulta ancora pagato", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("era in scadenza", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void English_Overdue_AndNotDue_AreTranslated()
    {
        var overdue = Render(English);
        var due = Render(English, overdue: false);

        Assert.Equal("Rent reminder - Bilocale Sparano, due 5 October 2026", overdue.Subject);
        Assert.Contains("Dear Giulia Verdi,", overdue.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("was due on <strong>5 October 2026</strong> and has not been paid yet", overdue.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("the rent of <strong>€1,234.50</strong>", overdue.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("is due by <strong>5 October 2026</strong> and has not been paid yet", due.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", overdue.HtmlBody, StringComparison.Ordinal);
        Assert.NotEqual(Render(EmailTemplates.DefaultCulture).Subject, overdue.Subject);
    }

    [Fact]
    public void WithoutALink_TheTenantPaysTheWayAgreedWithTheLandlord_AndThereIsNoButton()
    {
        var italian = Render(EmailTemplates.DefaultCulture);
        var english = Render(English);

        Assert.Contains("Per il pagamento usa il metodo che hai concordato con il locatore, Casa Rossi Srl.", italian.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Se hai già pagato, ignora questa email.", italian.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Paga il canone", italian.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<a href", italian.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Please pay with the method you agreed with the landlord, Casa Rossi Srl.", english.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("If you have already paid, please ignore this email.", english.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void WithALink_ThePersonalLinkIsTheButtonAndIsRepeatedForClientsWithoutButtons()
    {
        var italian = Render(EmailTemplates.DefaultCulture, payUrl: PayUrl);
        var english = Render(English, payUrl: PayUrl);

        Assert.Contains($"href=\"{PayUrl}\"", italian.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Paga il canone", italian.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("il pagamento va direttamente sul conto Stripe di Casa Rossi Srl", italian.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Il link è personale: non inoltrarlo.", italian.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Per il pagamento usa il metodo", italian.HtmlBody, StringComparison.Ordinal);
        // The button, and the link written out (as text and as its address) for clients that do not render buttons.
        Assert.Equal(3, Regex.Matches(italian.HtmlBody, Regex.Escape(PayUrl)).Count);
        Assert.Contains("Pay the rent", english.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("the payment goes directly to the Stripe account of Casa Rossi Srl", english.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNote_IsQuotedUnderItsLabel_WithTheLineBreaksKept()
    {
        var italian = Render(EmailTemplates.DefaultCulture, note: "Ciao Giulia,\nil bonifico di ottobre non è arrivato.");
        var english = Render(English, note: "Hello Giulia");

        Assert.Contains("<strong>Messaggio del locatore</strong><br />Ciao Giulia,<br />il bonifico di ottobre non è arrivato.", italian.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<strong>Message from the landlord</strong><br />Hello Giulia", english.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoNote_NoQuote(string? note)
    {
        Assert.DoesNotContain("Messaggio del locatore", Render(EmailTemplates.DefaultCulture, note: note).HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void EveryDynamicValue_IsHtmlEncoded_TheNoteTheNamesThePropertyAndTheLandlord(string cultureName)
    {
        var content = Render(
            CultureInfo.GetCultureInfo(cultureName),
            note: Payload,
            tenant: Payload,
            property: Payload,
            landlord: Payload);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("phish.example\">", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(EncodedPayload, content.HtmlBody, StringComparison.Ordinal);
        // The subject is plain text on one line.
        Assert.DoesNotContain('\n', content.Subject);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void NoPlaceholderIsLeftInTheDocument(string cultureName)
    {
        foreach (var overdue in new[] { true, false })
        {
            foreach (var payUrl in new string?[] { null, PayUrl })
            {
                var content = Render(CultureInfo.GetCultureInfo(cultureName), overdue, "Una nota", payUrl);

                Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
                Assert.StartsWith("<!DOCTYPE html>", content.HtmlBody, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ALinkThatIsNotHttp_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => Render(EmailTemplates.DefaultCulture, payUrl: "javascript:alert(1)"));
    }

    [Fact]
    public void TheTemplateName_IsStableForTheLogs()
    {
        Assert.Equal("rent-reminder", EmailTemplates.Names.RentReminder);
    }
}
