using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// SP-15a: the emails of the payment of a service (<see cref="EmailTemplates.ServicePaymentRequest"/>, the reminder, the receipt to
/// the supplier and the notice of an offline payment to the host), Italian and English, every dynamic value HTML-encoded.
/// Decision D24: the texts are true, and promise nothing about when the supplier is paid; the split is "price gross, CasaZen
/// commission, net".
/// </summary>
public class ServicePaymentEmailTemplatesTests
{
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";
    private const string PayUrl = "https://casazen-app.test/service/pay/6c1d2d36-1c3f-4a45-8d6b-1f4c8c6f0c11?token=Abc_123-xyz";
    private static readonly DateTime Instant = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> Cultures => new() { "it-IT", "en" };

    public static TheoryData<string> Templates => new() { "request", "reminder", "received", "offline" };

    private static EmailContent Render(string template, string culture, string text = "Pulizia", string? reason = "Pagato in contanti", int fee = 600) =>
        template switch
        {
            "request" => EmailTemplates.ServicePaymentRequest(Culture(culture), text, text, text, 6_000, Instant.AddDays(30), PayUrl),
            "reminder" => EmailTemplates.ServicePaymentReminder(Culture(culture), text, text, text, 6_000, Instant.AddDays(30), PayUrl),
            "received" => EmailTemplates.ServicePaymentReceived(
                Culture(culture), text, text, 6_000, 10m, fee, 6_000 - fee, Instant, "https://casazen-app.test/app/supplier/inbox"),
            "offline" => EmailTemplates.ServicePaymentOfflineRecorded(Culture(culture), text, text, text, 6_000, reason),
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };

    private static CultureInfo Culture(string name) => CultureInfo.GetCultureInfo(name);

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

    [Theory]
    [InlineData("request")]
    [InlineData("reminder")]
    public void PaymentRequestAndReminder_CarryTheLink_TheAmountAndTheValidity(string template)
    {
        var content = Render(template, "it-IT");

        // The personal link: the address of the button, then the address and the text of the fallback for clients that do not
        // render buttons.
        Assert.Equal(3, Regex.Matches(content.HtmlBody, Regex.Escape(PayUrl)).Count);
        Assert.Contains("60,00 €", content.HtmlBody);
        Assert.Contains("07/11/2026", content.HtmlBody); // 30 days after 8 October
        Assert.Contains("non inoltrarlo", content.HtmlBody);
        Assert.Contains("direttamente sul conto Stripe del fornitore", content.HtmlBody);
    }

    [Fact]
    public void PaymentRequest_English_SaysTheSameThings()
    {
        var content = Render("request", "en");

        Assert.Contains("60.00 €", content.HtmlBody);
        Assert.Contains("goes directly to the supplier's Stripe account", content.HtmlBody);
        Assert.Contains("do not forward it", content.HtmlBody);
        Assert.Contains("7 November 2026", content.HtmlBody);
    }

    [Fact]
    public void Receipt_ShowsPriceGrossCommissionAndNet_InThatOrder()
    {
        var content = Render("received", "it-IT");

        var gross = content.HtmlBody.IndexOf("Prezzo al lordo", StringComparison.Ordinal);
        var commission = content.HtmlBody.IndexOf("Commissione CasaZen (10 %)", StringComparison.Ordinal);
        var net = content.HtmlBody.IndexOf("Netto per te", StringComparison.Ordinal);
        Assert.True(gross >= 0 && commission > gross && net > commission, "gross, commission, net: in that order");
        Assert.Contains("60,00 €", content.HtmlBody);
        Assert.Contains("6,00 €", content.HtmlBody);
        Assert.Contains("54,00 €", content.HtmlBody);
        Assert.Contains("prima delle commissioni di Stripe", content.HtmlBody);
    }

    [Fact]
    public void Receipt_WithoutACommission_HasNoCommissionLine()
    {
        var content = Render("received", "it-IT", fee: 0);

        Assert.DoesNotContain("Commissione CasaZen", content.HtmlBody);
        Assert.Contains("Prezzo al lordo", content.HtmlBody);
        Assert.Contains("Netto per te", content.HtmlBody);
    }

    [Fact]
    public void Receipt_ADecimalPercentage_IsShownWithTheCultureOfTheEmail()
    {
        var italian = EmailTemplates.ServicePaymentReceived(Culture("it-IT"), "Pulizia", "Casa", 10_000, 7.5m, 750, 9_250, Instant, "https://casazen-app.test/x");
        var english = EmailTemplates.ServicePaymentReceived(Culture("en"), "Cleaning", "House", 10_000, 7.5m, 750, 9_250, Instant, "https://casazen-app.test/x");

        Assert.Contains("(7,5 %)", italian.HtmlBody);
        Assert.Contains("(7.5 %)", english.HtmlBody);
    }

    [Fact]
    public void OfflineNotice_ShowsTheReasonOfTheSupplier_AndOmitsItWhenThereIsNone()
    {
        var withReason = Render("offline", "it-IT", reason: "Il cliente ha pagato in contanti");
        var without = Render("offline", "it-IT", reason: null);

        Assert.Contains("Motivo indicato dal fornitore", withReason.HtmlBody);
        Assert.Contains("Il cliente ha pagato in contanti", withReason.HtmlBody);
        Assert.DoesNotContain("Motivo indicato dal fornitore", without.HtmlBody);
        Assert.Contains("contatta il fornitore", without.HtmlBody);
    }

    [Fact]
    public void Links_MustBeAbsoluteHttp_NoRelativeOrScriptLinkReachesAButton()
    {
        Assert.Throws<ArgumentException>(() =>
            EmailTemplates.ServicePaymentRequest(Culture("it-IT"), "a", "b", "c", 100, Instant, "javascript:alert(1)"));
        Assert.Throws<ArgumentException>(() =>
            EmailTemplates.ServicePaymentReminder(Culture("it-IT"), "a", "b", "c", 100, Instant, "/service/pay/1"));
    }

    // ─── Decision D24: true texts ───

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Texts_PromiseNothingAboutWhenTheSupplierIsPaidOrHowFast(string culture)
    {
        var forbidden = new Regex(
            @"(entro|within|in)\s+\d+\s*(giorn|ore\b|h\b|day|hour|minut)|garantit|guarantee|immediat|instant|subito|in tempo reale|real[- ]?time|same[- ]day|giorni lavorativi|business day",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var offending = ServicePaymentTexts(culture).Where(text => forbidden.IsMatch(text.Value)).Select(text => $"{text.Key}: {text.Value}").ToList();

        Assert.True(offending.Count == 0, "A promise of timing or speed in the payment emails (D24): " + string.Join(" | ", offending));
    }

    [Fact]
    public void Texts_OnlyTheSupplierIsToldAboutTheCommission_NeverThePayer()
    {
        // The payer pays the price shown; the commission is the supplier's. Only the receipt (to the supplier) names it.
        foreach (var culture in new[] { "it-IT", "en" })
        {
            foreach (var template in new[] { "request", "reminder" })
            {
                var body = Render(template, culture).HtmlBody;
                Assert.DoesNotMatch(@"\d\s*%", body);
            }
        }
    }

    [Fact]
    public void EveryKeyOfTheServicePaymentEmails_ExistsInItalianAndEnglish()
    {
        var italian = Keys("EmailTexts.resx");
        var english = Keys("EmailTexts.en.resx");

        var keys = italian.Where(key => key.StartsWith("ServicePayment", StringComparison.Ordinal)).ToList();
        Assert.True(keys.Count >= 20, "the payment texts are in the resources");
        Assert.Empty(keys.Except(english));
        Assert.Empty(english.Where(key => key.StartsWith("ServicePayment", StringComparison.Ordinal)).Except(italian));
        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(EmailTexts.Get(key, Culture("it-IT"))), key);
            Assert.False(string.IsNullOrWhiteSpace(EmailTexts.Get(key, Culture("en"))), key);
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ServicePaymentTexts(string culture)
    {
        var resources = EmailTexts.Resources;
        foreach (var key in Keys("EmailTexts.resx").Where(key => key.StartsWith("ServicePayment", StringComparison.Ordinal)))
        {
            yield return new KeyValuePair<string, string>(key, resources.GetString(key, Culture(culture)) ?? string.Empty);
        }
    }

    private static HashSet<string> Keys(string fileName)
    {
        var path = Path.Combine(SolutionRoot(), "Casazen.Infrastructure", "Email", "Templates", fileName);
        return XDocument.Load(path).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
