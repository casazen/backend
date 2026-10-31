using System.Collections;
using System.Globalization;
using System.Resources;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-10b-b: the rules of the offer on a request on measure, as pure functions. The ones that cost money when they are wrong are the ones tested:
/// the amount of a line is the quantity times the price of one and the total is what the lines add up to (the amount charged is the total of the
/// offer, never anything else), an offer outside the limits is refused whole, the offer is the quote of SP-04b (a quote written before the offer
/// keeps its JSON), and the states of the negotiation are derived from facts that are stored.
/// </summary>
public class ServiceRequestOfferRulesTests
{
    private static readonly DateTime SentAt = new(2026, 10, 9, 9, 0, 0, 123, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    // ─── Lines and total ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_TheAmountOfALineIsTheQuantityTimesThePriceOfOne_AndTheTotalAddsThemUp()
    {
        var (lines, total) = ServiceRequestOfferRules.Normalize(
        [
            new ServiceRequestOfferLine("  Pulizia a fondo  ", 3, 4500),
            new ServiceRequestOfferLine("Prodotti", 1, 1250),
        ]);

        Assert.Equal(
            new[] { new ServiceRequestQuoteLine("Pulizia a fondo", 13500, 3, 4500), new ServiceRequestQuoteLine("Prodotti", 1250, 1, 1250) },
            lines.ToArray());
        Assert.Equal(14750, total);
    }

    [Fact]
    public void Normalize_TheLimitsAreTheOnesOfTheQuote_AnOfferAtTheBoundIsValidAndOneCentMoreIsNot()
    {
        // The bound of a request is the bound of the catalog: 100,000 euro in cents, for a line and for the total.
        var bound = ServiceRequestLimits.MaxAmountCents;

        var (_, atTheBound) = ServiceRequestOfferRules.Normalize([new ServiceRequestOfferLine("Intero cantiere", 1, bound)]);
        Assert.Equal(bound, atTheBound);

        AssertInvalid([new ServiceRequestOfferLine("Intero cantiere", 1, bound + 1)]);
        AssertInvalid([new ServiceRequestOfferLine("A", 1, bound), new ServiceRequestOfferLine("B", 1, 1)]);

        // A line whose product is above the bound is refused, and a product above int is not a wrap-around to something valid.
        AssertInvalid([new ServiceRequestOfferLine("Molte ore", ServiceRequestOfferRules.MaxQuantity, bound)]);
        AssertInvalid([new ServiceRequestOfferLine("Molte ore", int.MaxValue, int.MaxValue)]);
    }

    [Fact]
    public void Normalize_AnOfferThatIsNotWhole_IsRefusedWhole()
    {
        AssertInvalid(null);
        AssertInvalid([]);
        AssertInvalid(Enumerable.Range(0, ServiceRequestLimits.MaxQuoteLines + 1).Select(i => new ServiceRequestOfferLine($"Voce {i}", 1, 100)).ToList());

        AssertInvalid([new ServiceRequestOfferLine("   ", 1, 100)]);
        AssertInvalid([new ServiceRequestOfferLine(null, 1, 100)]);
        AssertInvalid([new ServiceRequestOfferLine(new string('x', ServiceRequestLimits.QuoteLineLabelMaxLength + 1), 1, 100)]);
        AssertInvalid([new ServiceRequestOfferLine("Con\ttab", 1, 100)]);
        AssertInvalid([new ServiceRequestOfferLine("Zero", 0, 100)]);
        AssertInvalid([new ServiceRequestOfferLine("Troppi", ServiceRequestOfferRules.MaxQuantity + 1, 100)]);
        AssertInvalid([new ServiceRequestOfferLine("Gratis", 1, 0)]);
        AssertInvalid([new ServiceRequestOfferLine("Negativo", 1, -100)]);

        // One good line does not save a bad one next to it.
        AssertInvalid([new ServiceRequestOfferLine("Buona", 1, 100), new ServiceRequestOfferLine("Cattiva", 0, 100)]);
    }

    [Fact]
    public void ALineOfAnOffer_KeepsItsQuantityAndItsPrice_AndAQuoteOfSP04b_KeepsTheJsonItAlwaysHad()
    {
        // A quote written before the offer has no quantity: nothing about it changes in the column.
        Assert.Equal(
            "[{\"label\":\"Pulizia\",\"amountCents\":30000}]",
            ServiceRequestJson.Serialize(new[] { new ServiceRequestQuoteLine("Pulizia", 30000) }));

        var written = ServiceRequestJson.Serialize(new[] { new ServiceRequestQuoteLine("Posa", 9000, 2, 4500) });
        Assert.Equal(new ServiceRequestQuoteLine("Posa", 9000, 2, 4500), Assert.Single(ServiceRequestJson.ReadQuoteLines(written)));

        // A row written before SP-10b-b reads without them.
        Assert.Equal(
            new ServiceRequestQuoteLine("Vecchia", 100),
            Assert.Single(ServiceRequestJson.ReadQuoteLines("[{\"label\":\"Vecchia\",\"amountCents\":100}]")));

        // The lines the customer agreed to are the price lines of the work, with how many and at what price of one.
        var price = Assert.Single(ServiceRequestQuoteRules.AsPriceLines([new ServiceRequestQuoteLine("Posa", 9000, 2, 4500)]));
        Assert.Equal(new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Quote, "Posa", 9000, 2, 4500), price);
    }

    // ─── The offer that waits ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PendingOf_IsTheQuoteOfARequestOnMeasure_WithItsTimeAndTheDeadlineOfTheCustomer()
    {
        var request = OnMeasure();

        var offer = ServiceRequestOfferRules.PendingOf(request);

        Assert.NotNull(offer);
        Assert.Equal(SentAt, offer.SentAt);
        Assert.Equal(9000, offer.TotalCents);
        Assert.Equal(Start, offer.StartUtc);
        Assert.Equal(Start.AddHours(2), offer.EndUtc);
        Assert.Equal(SentAt.AddHours(24), offer.ValidUntil);
        Assert.Null(offer.Note);
        Assert.Equal(new ServiceRequestQuoteLine("Posa", 9000, 2, 4500), Assert.Single(offer.Lines));

        // The same facts are the quote of the engine: one engine, not two mechanisms (decision SP-D30).
        Assert.NotNull(ServiceRequestQuoteRules.PendingOf(request));
    }

    [Fact]
    public void PendingOf_IsNothingForABookingFromASlot_ARequestThatMovedOn_OrAnOfferWithoutATime()
    {
        Assert.Null(ServiceRequestOfferRules.PendingOf(OnMeasure(r => r.Kind = ServiceRequestKind.Standard)));
        Assert.Null(ServiceRequestOfferRules.PendingOf(OnMeasure(r => r.Status = ServiceRequestStatus.PresoInCarico)));
        Assert.Null(ServiceRequestOfferRules.PendingOf(OnMeasure(r => r.Status = ServiceRequestStatus.Annullato)));
        Assert.Null(ServiceRequestOfferRules.PendingOf(OnMeasure(r => r.QuoteSentAt = null)));
        Assert.Null(ServiceRequestOfferRules.PendingOf(OnMeasure(r => r.QuoteTotalCents = null)));
        Assert.Null(ServiceRequestOfferRules.PendingOf(OnMeasure(r => r.QuoteScheduledStartUtc = null)));
        Assert.Null(ServiceRequestOfferRules.PendingOf(OnMeasure(r => r.QuoteScheduledEndUtc = null)));
    }

    [Theory]
    [InlineData(ServiceRequestKind.Custom, ServiceRequestStatus.Richiesto, true, true)]
    [InlineData(ServiceRequestKind.Custom, ServiceRequestStatus.Richiesto, false, false)]
    [InlineData(ServiceRequestKind.Custom, ServiceRequestStatus.PresoInCarico, true, false)]
    [InlineData(ServiceRequestKind.Custom, ServiceRequestStatus.Rifiutato, true, false)]
    [InlineData(ServiceRequestKind.Custom, ServiceRequestStatus.Annullato, true, false)]
    [InlineData(ServiceRequestKind.Standard, ServiceRequestStatus.Richiesto, true, false)]
    public void CanSend_OnlyANewRequestOnMeasure_OfASupplierThatIsActive(
        ServiceRequestKind kind, ServiceRequestStatus status, bool supplierActive, bool expected)
    {
        Assert.Equal(expected, ServiceRequestOfferRules.CanSend(kind, status, supplierActive));
    }

    [Fact]
    public void LastOfferDeclined_IsTrueOnlyWhenTheLastThingThatHappenedToAnOfferIsTheCustomersNo()
    {
        static string Events(params string[] kinds) =>
            ServiceRequestJson.Serialize(kinds.Select((kind, i) => new ServiceRequestQuoteEvent(kind, SentAt.AddMinutes(i), 9000)).ToArray());

        Assert.False(ServiceRequestOfferRules.HasHistory(null));
        Assert.False(ServiceRequestOfferRules.HasHistory("[]"));
        Assert.True(ServiceRequestOfferRules.HasHistory(Events(ServiceRequestQuoteEventKinds.Sent)));

        Assert.False(ServiceRequestOfferRules.LastOfferDeclined(null));
        Assert.False(ServiceRequestOfferRules.LastOfferDeclined("[]"));
        Assert.False(ServiceRequestOfferRules.LastOfferDeclined(Events(ServiceRequestQuoteEventKinds.Sent)));
        Assert.True(ServiceRequestOfferRules.LastOfferDeclined(Events(ServiceRequestQuoteEventKinds.Sent, ServiceRequestQuoteEventKinds.Rejected)));

        // A new offer after the no, and an offer the supplier took back, are not "declined".
        Assert.False(ServiceRequestOfferRules.LastOfferDeclined(
            Events(ServiceRequestQuoteEventKinds.Sent, ServiceRequestQuoteEventKinds.Rejected, ServiceRequestQuoteEventKinds.Sent)));
        Assert.False(ServiceRequestOfferRules.LastOfferDeclined(
            Events(ServiceRequestQuoteEventKinds.Sent, ServiceRequestQuoteEventKinds.Withdrawn)));
    }

    // ─── Where the negotiation is ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, false, false, false, null, ServiceRequestNegotiation.AwaitingSupplier)]
    [InlineData(ServiceRequestStatus.Richiesto, true, false, false, null, ServiceRequestNegotiation.InConversation)]
    [InlineData(ServiceRequestStatus.Richiesto, true, true, false, null, ServiceRequestNegotiation.OfferSent)]
    [InlineData(ServiceRequestStatus.Richiesto, false, true, false, null, ServiceRequestNegotiation.OfferSent)]
    [InlineData(ServiceRequestStatus.Richiesto, true, false, true, null, ServiceRequestNegotiation.OfferDeclined)]
    [InlineData(ServiceRequestStatus.Richiesto, true, true, true, null, ServiceRequestNegotiation.OfferSent)]
    [InlineData(ServiceRequestStatus.PresoInCarico, true, false, false, null, ServiceRequestNegotiation.Agreed)]
    [InlineData(ServiceRequestStatus.InCorso, true, false, false, null, ServiceRequestNegotiation.Agreed)]
    [InlineData(ServiceRequestStatus.Rifiutato, true, false, false, null, ServiceRequestNegotiation.Declined)]
    [InlineData(ServiceRequestStatus.Annullato, true, false, false, ServiceRequestCancellationReasons.QuoteNotAnswered, ServiceRequestNegotiation.Expired)]
    [InlineData(ServiceRequestStatus.Annullato, false, false, false, ServiceRequestCancellationReasons.NoResponse, ServiceRequestNegotiation.Closed)]
    [InlineData(ServiceRequestStatus.Annullato, true, false, false, ServiceRequestCancellationReasons.CancelledByCustomer, ServiceRequestNegotiation.Closed)]
    [InlineData(ServiceRequestStatus.Annullato, true, false, false, null, ServiceRequestNegotiation.Closed)]
    public void Negotiation_IsDerivedFromTheStatusTheThreadTheOfferAndTheReasonOfTheCancellation(
        ServiceRequestStatus status,
        bool supplierWrote,
        bool offerPending,
        bool lastOfferDeclined,
        string? reason,
        string expected)
    {
        Assert.Equal(expected, ServiceRequestNegotiation.Of(status, supplierWrote, offerPending, lastOfferDeclined, reason));
    }

    [Fact]
    public void Negotiation_AnAcceptedOfferWhoseCardIsNotThrough_IsAwaitingPayment_OnlyWhileTheOfferStands()
    {
        Assert.Equal(
            ServiceRequestNegotiation.AwaitingPayment,
            ServiceRequestNegotiation.Of(ServiceRequestStatus.Richiesto, true, offerPending: true, awaitingPayment: true));

        // Without a standing offer, or once the request moved on, a payment says nothing about the negotiation.
        Assert.Equal(
            ServiceRequestNegotiation.InConversation,
            ServiceRequestNegotiation.Of(ServiceRequestStatus.Richiesto, true, offerPending: false, awaitingPayment: true));
        Assert.Equal(
            ServiceRequestNegotiation.Agreed,
            ServiceRequestNegotiation.Of(ServiceRequestStatus.PresoInCarico, true, offerPending: false, awaitingPayment: true));
    }

    // ─── The card of an offer ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(12000, 0, true)] // made for the total, after the offer was sent
    [InlineData(12000, 30, true)]
    [InlineData(12000, -1, false)] // made before the offer was sent: the card of an offer that was replaced
    [InlineData(11999, 0, false)] // another total
    [InlineData(12001, 5, false)]
    public void IsPaymentOfOffer_OnlyForTheTotalOfTheOffer_AndCreatedAfterTheSupplierSentIt(int paymentCents, int createdMinutesAfterTheOffer, bool expected)
    {
        Assert.Equal(
            expected,
            ServiceRequestOfferRules.IsPaymentOfOffer(SentAt.AddMinutes(createdMinutesAfterTheOffer), paymentCents, SentAt, 12000));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested, "awaiting_card")]
    [InlineData(ServicePaymentStatus.Failed, "awaiting_card")]
    [InlineData(ServicePaymentStatus.Authorized, "authorized")]
    [InlineData(ServicePaymentStatus.Held, "held")]
    public void PaymentStatusOf_TheCardOfAnAcceptedOfferIsAwaitedUntilItIsBlocked(ServicePaymentStatus status, string expected)
    {
        Assert.Equal(expected, ShowcaseBookingManagementRules.PaymentStatusOf(status));
    }

    // ─── Codes and texts ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MessageKeys_EveryKeyOfTheOffer_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.Equal(ServiceRequestOfferErrors.MessageKeys.Count, ServiceRequestOfferErrors.MessageKeys.Distinct().Count());
        Assert.All(ServiceRequestOfferErrors.MessageKeys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });

        // The messages of the validation of the body (the attributes of the DTOs name them).
        foreach (var key in new[] { "ServiceRequestOfferQuantityOutOfRange", "ServiceRequestOfferStartRequired", "ServiceRequestOfferSentAtRequired" })
        {
            Assert.True(italian.ContainsKey(key) && english.ContainsKey(key), key);
            Assert.NotEqual(italian[key], english[key]);
        }
    }

    [Fact]
    public void TheCodesOfTheOffer_AreSnakeCase_AndDifferFromTheOnesOfTheQuote()
    {
        var codes = new[]
        {
            ServiceRequestOfferErrors.Invalid, ServiceRequestOfferErrors.CannotOffer, ServiceRequestOfferErrors.NoOfferToWithdraw,
            ServiceRequestOfferErrors.NoOffer, ServiceRequestOfferErrors.OfferExpired, ServiceRequestOfferErrors.OfferChanged,
        };

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[a-z]+(_[a-z]+)+$", code));
        Assert.DoesNotContain(ShowcaseBookingQuoteErrors.NoQuote, codes);
        Assert.DoesNotContain(Casazen.Core.Services.ServiceRequestErrorCodes.QuoteChanged, codes);
    }

    // ─── helpers ───

    private static void AssertInvalid(IReadOnlyList<ServiceRequestOfferLine>? lines)
    {
        var refusal = Assert.Throws<DomainRuleException>(() => ServiceRequestOfferRules.Normalize(lines));
        Assert.Equal(ServiceRequestOfferErrors.Invalid, refusal.Code);
    }

    private static ServiceRequest OnMeasure(Action<ServiceRequest>? change = null)
    {
        var request = new ServiceRequest
        {
            RentalContext = ServiceRequestRentalContext.Showcase,
            Kind = ServiceRequestKind.Custom,
            Status = ServiceRequestStatus.Richiesto,
            QuoteSentAt = SentAt,
            QuoteLinesJson = ServiceRequestJson.Serialize(new[] { new ServiceRequestQuoteLine("Posa", 9000, 2, 4500) }),
            QuoteTotalCents = 9000,
            QuoteNote = "   ",
            QuoteScheduledStartUtc = Start,
            QuoteScheduledEndUtc = Start.AddHours(2),
            ResponseDueAt = SentAt.AddHours(24),
        };
        change?.Invoke(request);
        return request;
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No SharedResources for {culture.Name}");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }
}
