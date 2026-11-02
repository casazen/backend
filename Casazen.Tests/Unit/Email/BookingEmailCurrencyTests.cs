using System.Globalization;
using System.Xml.Linq;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// CHF-MONEY: the booking e-mails say the amounts in the currency of the property. The texts used to write the euro sign themselves
/// (<c>{0} €</c>, <c>€{0}</c>), so a guest of a house in francs was told "Totale: 1.262,00 €" of a charge of CHF 1262: the texts now
/// carry no symbol and the amount they are given has its currency. The euro is written exactly as before.
/// </summary>
public class BookingEmailCurrencyTests
{
    private const string Link = "https://casazen-app.test/app/x";
    private static readonly DateTime CheckIn = new(2026, 10, 5);
    private static readonly BookingHostContact Host = new("Casa Lago Sagl", "info@casalago.test");

    /// <summary>The booking keys whose texts used to carry the euro sign; the rent keys (long term, euros only) are not among them.</summary>
    public static TheoryData<string> BookingKeysOnceInEuros => new()
    {
        "GuestRefundConfirmed_Body", "GuestBookingCancelled_Refund", "GuestPaymentRefundedDatesUnavailable_Refund",
        "OnSiteRequestReceived_Body", "OnSiteRequestToHost_Body", "Booking_Summary_Lodging", "Booking_Summary_Cleaning",
        "Booking_Summary_TouristTax", "Booking_Summary_Total", "GuestBookingConfirmed_Paid", "GuestBookingConfirmed_DeferredOn",
        "GuestBookingConfirmed_Deferred", "GuestBookingConfirmed_OnSite", "HostBookingConfirmed_Paid", "HostBookingConfirmed_DeferredOn",
        "HostBookingConfirmed_Deferred", "GuestBookingCancelled_Refunded", "GuestDeferredChargeFailed_Body",
        "HostDeferredChargeFailed_Body", "GuestBookingCancelled_Retained", "HostBookingCancelledByGuest_Refund",
        "HostBookingCancelledByGuest_Retained",
    };

    [Theory]
    [MemberData(nameof(BookingKeysOnceInEuros))]
    public void TheTextsOfTheBookingEmails_CarryNoEuroSignOfTheirOwn(string key)
    {
        foreach (var file in new[] { "EmailTexts.resx", "EmailTexts.en.resx" })
        {
            var path = Path.Combine(FindRepositoryRoot(), "Casazen.Infrastructure", "Email", "Templates", file);
            var value = XDocument.Load(path).Root!.Elements("data")
                .Single(data => (string?)data.Attribute("name") == key)
                .Element("value")!.Value;

            Assert.DoesNotContain("€", value, StringComparison.Ordinal);
            Assert.DoesNotContain("EUR", value, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("it-IT", "CHF", "<li>Soggiorno: CHF 1.200,00</li>", "<li>Totale: <strong>CHF 1.262,00</strong></li>", "Pagamento ricevuto: <strong>CHF 1.262,00</strong>.")]
    [InlineData("en", "CHF", "<li>Stay: CHF 1,200.00</li>", "<li>Total: <strong>CHF 1,262.00</strong></li>", "Payment received: <strong>CHF 1,262.00</strong>.")]
    [InlineData("it-IT", "EUR", "<li>Soggiorno: 1.200,00 €</li>", "<li>Totale: <strong>1.262,00 €</strong></li>", "Pagamento ricevuto: <strong>1.262,00 €</strong>.")]
    [InlineData("en", "EUR", "<li>Stay: €1,200.00</li>", "<li>Total: <strong>€1,262.00</strong></li>", "Payment received: <strong>€1,262.00</strong>.")]
    public void GuestBookingConfirmed_SaysEveryAmountInTheCurrencyOfTheProperty(
        string culture, string currency, string lodging, string total, string paid)
    {
        var content = EmailTemplates.GuestBookingConfirmed(
            CultureInfo.GetCultureInfo(culture), "Anna", Summary(currency), BookingConfirmationKind.PaidOnline, 1262m, null, Host, Link);

        Assert.Contains(lodging, content.HtmlBody);
        Assert.Contains(total, content.HtmlBody);
        Assert.Contains(paid, content.HtmlBody);
        if (currency == "CHF")
            Assert.DoesNotContain("€", content.HtmlBody);
    }

    [Fact]
    public void HostBookingConfirmed_AndTheDeferredCharge_SayFrancs()
    {
        var paid = EmailTemplates.HostBookingConfirmed(
            EmailTemplates.DefaultCulture, "Anna Verdi", Summary("CHF"), BookingConfirmationKind.PaidOnline, 1262m, null, Link);
        var deferred = EmailTemplates.HostBookingConfirmed(
            EmailTemplates.DefaultCulture, "Anna Verdi", Summary("CHF"), BookingConfirmationKind.DeferredCharge, 0m, CheckIn.AddDays(-7), Link);

        Assert.Contains("Pagamento online ricevuto: <strong>CHF 1.262,00</strong>.", paid.HtmlBody);
        Assert.Contains("il totale di <strong>CHF 1.262,00</strong> sarà addebitato il", deferred.HtmlBody);
        Assert.DoesNotContain("€", paid.HtmlBody);
        Assert.DoesNotContain("€", deferred.HtmlBody);
    }

    [Fact]
    public void RefundEmails_SayTheAmountRefundedInFrancs()
    {
        var confirmed = EmailTemplates.GuestRefundConfirmed(EmailTemplates.DefaultCulture, "Anna", "Casa Lago", CheckIn, 400m, "CHF");
        var late = EmailTemplates.GuestPaymentRefundedDatesUnavailable(
            EmailTemplates.DefaultCulture, "Anna", "Casa Lago", CheckIn, CheckIn.AddDays(3), 400m, "CHF");
        var euros = EmailTemplates.GuestRefundConfirmed(EmailTemplates.DefaultCulture, "Anna", "Casa Rosa", CheckIn, 400m);

        Assert.Contains("abbiamo rimborsato <strong>CHF 400,00</strong>", confirmed.HtmlBody);
        Assert.Contains("Ti abbiamo rimborsato l'intero importo pagato, <strong>CHF 400,00</strong>.", late.HtmlBody);
        Assert.DoesNotContain("€", confirmed.HtmlBody);
        Assert.DoesNotContain("€", late.HtmlBody);
        Assert.Contains("abbiamo rimborsato <strong>400,00 €</strong>", euros.HtmlBody);
    }

    [Fact]
    public void CancellationByTheGuest_SaysRefundedStartedAndKeptAmountsInFrancs_ToTheGuestAndToTheHost()
    {
        var guest = EmailTemplates.GuestBookingCancelled(
            EmailTemplates.DefaultCulture, "Anna", "Casa Lago", CheckIn, CheckIn.AddDays(3), 100m, 50m, Host,
            BookingCancellationEmailCause.Guest, 20m, "CHF");
        var host = EmailTemplates.HostBookingCancelledByGuest(
            EmailTemplates.DefaultCulture, "Anna Verdi", Summary("CHF"), 150m, 20m, Link);

        Assert.Contains("Ti abbiamo rimborsato <strong>CHF 100,00</strong>.", guest.HtmlBody);
        Assert.Contains("rimborso di <strong>CHF 50,00</strong>", guest.HtmlBody);
        Assert.Contains("<strong>CHF 20,00</strong> non vengono rimborsati", guest.HtmlBody);
        Assert.Contains("<strong>CHF 150,00</strong>", host.HtmlBody);
        Assert.Contains("Importo che resta a te: <strong>CHF 20,00</strong>.", host.HtmlBody);
        Assert.DoesNotContain("€", guest.HtmlBody);
        Assert.DoesNotContain("€", host.HtmlBody);
    }

    [Fact]
    public void PayAtThePropertyRequests_SayTheAmountInFrancs()
    {
        var toGuest = EmailTemplates.OnSiteRequestReceived(
            EmailTemplates.DefaultCulture, "Anna", "Casa Lago", CheckIn, CheckIn.AddDays(3), 350m, Link, CheckIn.AddDays(-5), "CHF");
        var toHost = EmailTemplates.OnSiteRequestToHost(
            EmailTemplates.DefaultCulture, "Anna Verdi", "Casa Lago", CheckIn, CheckIn.AddDays(3), 2, 350m, CheckIn.AddDays(-4), Link, "CHF");

        Assert.Contains("<strong>CHF 350,00</strong>", toGuest.HtmlBody);
        Assert.Contains("<strong>CHF 350,00</strong>", toHost.HtmlBody);
        Assert.DoesNotContain("€", toGuest.HtmlBody);
        Assert.DoesNotContain("€", toHost.HtmlBody);
    }

    [Fact]
    public void DeferredChargeFailed_SaysTheAmountInFrancs_ToTheGuestAndToTheHost()
    {
        var guest = EmailTemplates.GuestDeferredChargeFailed(
            EmailTemplates.DefaultCulture, "Anna", Summary("CHF"), Link, CheckIn.AddDays(-2), Host);
        var host = EmailTemplates.HostDeferredChargeFailed(
            EmailTemplates.DefaultCulture, "Anna Verdi", Summary("CHF"), guestAsked: true, CheckIn.AddDays(-1), Link);

        Assert.Contains("<strong>CHF 1.262,00</strong>", guest.HtmlBody);
        Assert.Contains("<strong>CHF 1.262,00</strong>", host.HtmlBody);
        Assert.DoesNotContain("€", guest.HtmlBody);
        Assert.DoesNotContain("€", host.HtmlBody);
    }

    private static BookingEmailSummary Summary(string currency) => new(
        "b7d3c1f0-0000-4000-8000-000000000001", "Casa Lago", CheckIn, CheckIn.AddDays(3), 2, 1200m, 50m, 12m, 1262m, currency);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
