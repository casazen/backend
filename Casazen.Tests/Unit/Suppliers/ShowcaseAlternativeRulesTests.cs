using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-10c: when a customer is offered other suppliers (only when CasaZen closed its request because the supplier did not answer in time), which
/// supplier qualifies (a published showcase, not paused, that takes bookings) and which of its services corresponds to the request.
/// </summary>
public class ShowcaseAlternativeRulesTests
{
    [Theory]
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.System, ServiceRequestCancellationReasons.NoResponse, true)]
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.System, ServiceRequestCancellationReasons.QuoteNotAnswered, false)] // the supplier did answer: the customer did not
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.System, ServiceRequestCancellationReasons.ProposalNotAnswered, false)]
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.Customer, ServiceRequestCancellationReasons.CancelledByCustomer, false)]
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.Supplier, null, false)] // it answered: it cannot do it
    [InlineData(ServiceRequestStatus.Annullato, ServiceRequestActorParty.System, null, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, ServiceRequestActorParty.Supplier, null, false)]
    [InlineData(ServiceRequestStatus.Richiesto, null, null, false)]
    [InlineData(ServiceRequestStatus.PresoInCarico, null, null, false)]
    public void TheAlternatives_AreTheWayOutOfASilence_NotAListOfSuppliersBehindEveryRequest(
        ServiceRequestStatus status,
        ServiceRequestActorParty? cancelledBy,
        string? reason,
        bool expected)
    {
        Assert.Equal(expected, ShowcaseAlternativeRules.Offers(status, cancelledBy, reason));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)] // paused: it takes no booking
    [InlineData(false, false, false)] // published but its bookings are not open (no account, payments off, online booking off)
    public void ASupplier_QualifiesOnlyWithAPublishedShowcaseThatIsNotPausedAndTakesBookings(bool paused, bool bookingsOpen, bool expected)
    {
        var page = new PublishedShowcasePage(new ShowcaseContentDocument(), paused, bookingsOpen, Verified: true, JobsDone: null, 180, bookingsOpen, null);

        Assert.Equal(expected, ShowcaseAlternativeRules.Qualifies(page));
    }

    [Fact]
    public void ASupplierWithNothingPublished_DoesNotQualify()
    {
        Assert.False(ShowcaseAlternativeRules.Qualifies(null));
    }

    [Fact]
    public void TheService_IsTheCheapestPricedOneOfTheCategory_AServiceOnQuoteComesAfterThePricedOnes()
    {
        var services = new[]
        {
            Service("pulizia-profonda", "Pulizia profonda", "cleaning", 15000),
            Service("pulizia-casa", "Pulizia casa", "cleaning", 8000, SupplierServicePriceUnit.PerHour),
            Service("su-misura", "Pulizie su misura", "cleaning", null, requiresQuote: true),
            Service("riparazione", "Riparazione", "plumbing", 3000),
        };

        var chosen = ShowcaseAlternativeRules.ServiceFor(services, "cleaning");

        Assert.Equal(new ShowcaseAlternativeService("pulizia-casa", "Pulizia casa", 8000, SupplierServicePriceUnit.PerHour, RequiresQuote: false), chosen);
    }

    [Fact]
    public void TheService_WhenOnlyOnQuoteOnesExist_IsOneOfThemWithNoPrice()
    {
        var chosen = ShowcaseAlternativeRules.ServiceFor([Service("su-misura", "Pulizie su misura", "cleaning", null, requiresQuote: true)], "cleaning");

        Assert.NotNull(chosen);
        Assert.Null(chosen.FromPriceCents);
        Assert.True(chosen.RequiresQuote);
    }

    [Fact]
    public void TheService_ASupplierWithNoneInTheCategory_HasNoCorrespondingService()
    {
        Assert.Null(ShowcaseAlternativeRules.ServiceFor([Service("riparazione", "Riparazione", "plumbing", 3000)], "cleaning"));
        Assert.Null(ShowcaseAlternativeRules.ServiceFor([], "cleaning"));
    }

    private static SupplierPublicService Service(
        string slug,
        string name,
        string category,
        int? priceFromCents,
        SupplierServicePriceUnit unit = SupplierServicePriceUnit.PerJob,
        bool requiresQuote = false) =>
        new(
            slug,
            name,
            category,
            null,
            null,
            priceFromCents,
            unit,
            PricesIncludeVat: true,
            requiresQuote,
            DurationMinutes: 120,
            [],
            [],
            [],
            [],
            MinNoticeHours: null,
            SupplierServiceWeekdays.AllMask);
}
