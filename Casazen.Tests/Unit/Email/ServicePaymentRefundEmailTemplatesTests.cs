using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// SP-15b: the emails added by the webhook, the refunds and the admin tools: the payment that failed afterwards (to the payer, with a
/// new link), the refund (to the payer and to the supplier) and the alert to the platform admins. Italian and English, every dynamic
/// value HTML-encoded. Decision D24: true texts, no promise about timing; the payer is never told about the commission, and the
/// alert to the admins carries ids, amounts and codes, no name, address or contact of the payer.
/// </summary>
public class ServicePaymentRefundEmailTemplatesTests
{
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";
    private const string PayUrl = "https://casazen-app.test/service/pay/6c1d2d36-1c3f-4a45-8d6b-1f4c8c6f0c11?token=New_456-xyz";
    private const string ConsoleUrl = "https://casazen-app.test/app/supplier/inbox";
    private static readonly DateTime Instant = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid PaymentId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid RequestId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    public static TheoryData<string> Templates => new() { "failed", "refund-payer", "refund-supplier", "alert-review", "alert-dispute" };

    private static CultureInfo Culture(string name) => CultureInfo.GetCultureInfo(name);

    private static EmailContent Render(
        string template,
        string culture,
        string text = "Pulizia",
        string? detail = "review:amount",
        int commissionRefunded = 300) =>
        template switch
        {
            "failed" => EmailTemplates.ServicePaymentFailed(Culture(culture), text, text, text, 6_000, Instant.AddDays(30), PayUrl),
            "refund-payer" => EmailTemplates.ServicePaymentRefundedToPayer(Culture(culture), text, text, text, 1_500),
            "refund-supplier" => EmailTemplates.ServicePaymentRefundedToSupplier(Culture(culture), text, text, 1_500, commissionRefunded, ConsoleUrl),
            "alert-review" => EmailTemplates.ServicePaymentAdminAlert(Culture(culture), false, PaymentId, RequestId, text, 6_000, detail),
            "alert-dispute" => EmailTemplates.ServicePaymentAdminAlert(Culture(culture), true, PaymentId, RequestId, text, 6_000, detail),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };

    [Theory]
    [MemberData(nameof(Templates))]
    public void Render_PayloadInEveryDynamicValue_IsHtmlEncoded(string template)
    {
        var content = Render(template, "it-IT", Payload, Payload);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.DoesNotContain("phish.example\">", content.HtmlBody);
        Assert.Contains(EncodedPayload, content.HtmlBody);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Render_ItalianAndEnglish_ProduceCompleteLocalizedDocuments(string template)
    {
        var italian = Render(template, "it-IT");
        var english = Render(template, "en");

        Assert.StartsWith("<!DOCTYPE html>", italian.HtmlBody);
        Assert.Contains("<html lang=\"it\">", italian.HtmlBody);
        Assert.Contains("<html lang=\"en\">", english.HtmlBody);
        Assert.NotEqual(italian.Subject, english.Subject);
        foreach (var content in new[] { italian, english })
        {
            Assert.False(string.IsNullOrWhiteSpace(content.Subject));
            // No placeholder is left unfilled and no resource key shows through.
            Assert.DoesNotMatch(@"\{\d+\}", content.HtmlBody);
            Assert.DoesNotMatch(@"\{\d+\}", content.Subject);
            Assert.DoesNotMatch(@"ServicePayment\w*_\w+", content.HtmlBody);
            Assert.DoesNotContain("<", content.Subject);
            Assert.Contains("Pulizia", content.HtmlBody);
        }
    }

    [Fact]
    public void Failed_CarriesTheNewLink_TheAmountAndTheValidity_AndSaysNothingOfTheCommission()
    {
        var content = Render("failed", "it-IT");

        // The new personal link: the address of the button, then the address and the text of the fallback.
        Assert.Equal(3, Regex.Matches(content.HtmlBody, Regex.Escape(PayUrl)).Count);
        Assert.Contains("non è andato a buon fine", content.HtmlBody);
        Assert.Contains("60,00 €", content.HtmlBody);
        Assert.Contains("07/11/2026", content.HtmlBody);
        Assert.Contains("non inoltrarlo", content.HtmlBody);
        Assert.DoesNotMatch(@"\d\s*%", content.HtmlBody);
        Assert.Contains("Pagamento non riuscito: Pulizia", content.Subject);
    }

    [Fact]
    public void Failed_English_SaysTheSameThings()
    {
        var content = Render("failed", "en");

        Assert.Contains("did not go through", content.HtmlBody);
        Assert.Contains("60.00 €", content.HtmlBody);
        Assert.Equal(3, Regex.Matches(content.HtmlBody, Regex.Escape(PayUrl)).Count);
        Assert.Equal("Payment failed: Pulizia", content.Subject);
    }

    [Fact]
    public void Failed_NeedsAnAbsoluteHttpLink()
    {
        Assert.Throws<ArgumentException>(() =>
            EmailTemplates.ServicePaymentFailed(Culture("it-IT"), "a", "b", "c", 100, Instant, "javascript:alert(1)"));
    }

    [Theory]
    [InlineData("it-IT", "15,00 €", "dipendono dalla tua banca")]
    [InlineData("en", "15.00 €", "depends on your bank")]
    public void RefundToThePayer_SaysHowMuchWasRefunded_AndNothingAboutTheCommissionOrTheTiming(string culture, string amount, string honestHint)
    {
        var content = Render("refund-payer", culture);

        Assert.Contains(amount, content.HtmlBody);
        Assert.Contains(honestHint, content.HtmlBody);
        // The payer paid the price shown: the commission is not his business, and the refund has no date promised.
        Assert.DoesNotMatch(@"\d\s*%", content.HtmlBody);
        Assert.DoesNotContain("ommission", content.HtmlBody);
        Assert.DoesNotContain("PaymentIntent", content.HtmlBody);
    }

    [Fact]
    public void RefundToTheSupplier_WithACommission_ShowsTheShareThatCameBack_AndTheLinkToTheConsole()
    {
        var content = Render("refund-supplier", "it-IT", commissionRefunded: 300);

        Assert.Contains("15,00 €", content.HtmlBody);
        Assert.Contains("Insieme al rimborso ti torna la parte corrispondente della commissione CasaZen", content.HtmlBody);
        Assert.Contains("3,00 €", content.HtmlBody);
        Assert.Contains("saldo del tuo account Stripe", content.HtmlBody);
        Assert.Contains(ConsoleUrl, content.HtmlBody);
    }

    [Fact]
    public void RefundToTheSupplier_WithoutACommission_HasNoLineAboutIt()
    {
        var italian = Render("refund-supplier", "it-IT", commissionRefunded: 0);
        var english = Render("refund-supplier", "en", commissionRefunded: 0);

        Assert.DoesNotContain("Insieme al rimborso", italian.HtmlBody);
        Assert.DoesNotContain("matching part of CasaZen", english.HtmlBody);
        Assert.Contains("15,00 €", italian.HtmlBody);
        Assert.Contains("15.00 €", english.HtmlBody);
    }

    [Fact]
    public void RefundToTheSupplier_NeedsAnAbsoluteHttpLink()
    {
        Assert.Throws<ArgumentException>(() =>
            EmailTemplates.ServicePaymentRefundedToSupplier(Culture("it-IT"), "a", "b", 100, 10, "/app/supplier"));
    }

    [Fact]
    public void AdminAlert_ForAPaymentToReview_SaysTheRequestWasNotMarkedPaid_AndWhatToCheck()
    {
        var content = Render("alert-review", "it-IT", detail: "review:amount+fee");

        Assert.Contains("non è stato registrato come pagato", content.HtmlBody);
        Assert.Contains("La richiesta non è stata segnata come pagata", content.HtmlBody);
        Assert.Contains("Dashboard di Stripe", content.HtmlBody);
        Assert.Contains("Pagamento servizio da verificare: Pulizia", content.Subject);
        Assert.Contains("Dettaglio: review:amount", content.HtmlBody);
        Assert.Contains("fee", content.HtmlBody);
    }

    [Fact]
    public void AdminAlert_ForADispute_SaysThePaymentStaysAsItIs_AndWhereToAnswer()
    {
        var content = Render("alert-dispute", "it-IT", detail: "dp_123 fraudulent");

        Assert.Contains("ha contestato un pagamento", content.HtmlBody);
        Assert.Contains("Su CasaZen il pagamento resta com", content.HtmlBody);
        Assert.Contains("Rispondi alla contestazione dalla Dashboard di Stripe", content.HtmlBody);
        Assert.Contains("Contestazione su un pagamento servizio: Pulizia", content.Subject);
        Assert.Contains("dp_123 fraudulent", content.HtmlBody);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void AdminAlert_CarriesIdsAndAmounts_NoPersonalDataOfThePayer(string culture)
    {
        foreach (var template in new[] { "alert-review", "alert-dispute" })
        {
            var body = Render(template, culture).HtmlBody;

            Assert.Contains(PaymentId.ToString("D"), body);
            Assert.Contains(RequestId.ToString("D"), body);
            Assert.Contains(culture == "en" ? "60.00 €" : "60,00 €", body);
            Assert.DoesNotContain("@", body);
            Assert.DoesNotContain("https://", body);
        }

        // The signature itself keeps the payer out: ids, the supplier's name, the amount and a code.
        var parameters = typeof(EmailTemplates).GetMethod(nameof(EmailTemplates.ServicePaymentAdminAlert))!.GetParameters().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "culture", "dispute", "paymentId", "requestId", "supplierName", "amountCents", "detail" }, parameters);
    }

    [Fact]
    public void AdminAlert_WithoutADetail_HasNoDetailLine()
    {
        Assert.DoesNotContain("Dettaglio:", Render("alert-review", "it-IT", detail: null).HtmlBody);
        Assert.DoesNotContain("Dettaglio:", Render("alert-review", "it-IT", detail: "   ").HtmlBody);
        Assert.DoesNotContain("Detail:", Render("alert-review", "en", detail: null).HtmlBody);
    }
}
