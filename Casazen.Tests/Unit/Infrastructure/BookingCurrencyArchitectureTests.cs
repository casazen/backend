using System.Text.RegularExpressions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// CHF-MONEY (2026-10-30): a stay is priced, charged, refunded and shown in the currency of its property
/// (<c>StayCurrency</c>, <c>Property.Currency</c>: CHF for a house in Switzerland, EUR for any other). A house in francs used to be
/// shown in francs by the search and quoted, charged and refunded in euros, because the services of the booking path wrote <c>"EUR"</c>
/// (or <c>"eur"</c>) by hand. None of the files below may do it again: the code comes from the property, through
/// <c>StayCurrency</c>, and travels on the object (quote, booking view, cancellation preview, payment).
/// </summary>
public class BookingCurrencyArchitectureTests
{
    /// <summary>
    /// The files of the direct booking path: the quote, the checkout and its payment, the deferred charge, the webhooks, the refunds, the
    /// cancellation of the guest and of the host, the guest area, the public pages of a house and the e-mails of a booking.
    /// </summary>
    private static readonly string[] MoneyPathFiles =
    [
        "Casazen.Infrastructure/Services/BookingService.cs",
        "Casazen.Infrastructure/Services/BookingCancellationService.cs",
        "Casazen.Infrastructure/Services/GuestBookingCancellationService.cs",
        "Casazen.Infrastructure/Services/GuestBookingLookupService.cs",
        "Casazen.Infrastructure/Services/CheckoutOutcomeService.cs",
        "Casazen.Infrastructure/Services/CheckoutPaymentSettlementService.cs",
        "Casazen.Infrastructure/Services/DeferredChargeService.cs",
        "Casazen.Infrastructure/Services/PaymentRefundService.cs",
        "Casazen.Infrastructure/Services/BookingNotifier.cs",
        "Casazen.Infrastructure/Services/OnSiteBookingRequestService.cs",
        "Casazen.Infrastructure/External/StripeService.cs",
        "Casazen.Infrastructure/External/StripeWebhookHandler.cs",
        "Casazen.Web/Controllers/BookingController.cs",
        "Casazen.Web/Controllers/PublicBookingsController.cs",
    ];

    // A currency written as a string: "EUR", "eur" (what a service gives Stripe) or a mix.
    private static readonly Regex EuroLiteral = new("\"[eE][uU][rR]\"", RegexOptions.Compiled);

    [Fact]
    public void TheBookingMoneyPath_NeverWritesTheEuroByHand()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var relative in MoneyPathFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{relative} no longer exists: update the list of this test.");

            if (EuroLiteral.IsMatch(StripComments(File.ReadAllText(path))))
                offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "A currency written as \"EUR\" in the booking money path charges, refunds or shows a house in francs in euros. Take it from the " +
            "property: StayCurrency.ForProperty / ForStripe. Offending files: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheDetector_SeesAStringOfTheEuro_AndIgnoresCommentsAndOtherWords()
    {
        Assert.Matches(EuroLiteral, StripComments("var currency = \"EUR\";"));
        Assert.Matches(EuroLiteral, StripComments("Currency = \"eur\","));
        Assert.DoesNotMatch(EuroLiteral, StripComments("// the price was \"EUR\" before CHF-MONEY"));
        Assert.DoesNotMatch(EuroLiteral, StripComments("/// <c>\"EUR\"</c> or <c>\"CHF\"</c>"));
        Assert.DoesNotMatch(EuroLiteral, StripComments("var currency = StayCurrency.ForProperty(property);"));
        Assert.DoesNotMatch(EuroLiteral, StripComments("var europe = \"Europe/Rome\";"));
    }

    /// <summary>Removes <c>//</c> and <c>///</c> lines and <c>/* */</c> blocks: what a comment says about the euro is not a use of it.</summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
