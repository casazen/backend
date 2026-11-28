using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public sealed class ComoMonthlyDeclarationTests
{
    private static readonly StayTouristTaxView Tax = new(
        Guid.NewGuid(), TouristTaxQuoteStatus.Calculated, TouristTaxRateSource.Official, false, 6, 4, true, [], 12, 12,
        TouristTaxCollector.Host, default, false,
        [new(1, 6, 4, 12, TouristTaxExemptionKind.None, null), new(2, 6, 0, 0, TouristTaxExemptionKind.Host, "health_care")],
        [], DateTime.UtcNow, false);

    [Fact]
    public void SelectsByRomeCheckoutMonthWithinPropertyAndOrgAndExcludesNoShows()
    {
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        var end = new DateTime(2026, 12, 31, 23, 0, 0, DateTimeKind.Utc);
        Booking Departure(Guid tenant, Guid place, DateTime checkout) => new()
        {
            OrgId = tenant,
            PropertyId = place,
            CheckInDate = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = checkout,
            Status = BookingStatus.CheckedOut,
        };
        var december = Departure(org, property, end.AddDays(-29));
        var noShow = Departure(org, property, end.AddDays(-1));
        noShow.NoShowAt = noShow.CheckInDate;
        var stays = new[] { december, noShow, Departure(org, property, end), Departure(Guid.NewGuid(), property, end.AddDays(-1)), Departure(org, Guid.NewGuid(), end.AddDays(-1)) };
        var result = ComoMonthlyDeclaration.DeparturesInMonth(stays.AsQueryable(), org, property, new DateOnly(2026, 12, 1));
        Assert.Same(december, Assert.Single(result));
    }

    [Fact]
    public void EarlyCheckoutBelongsToItsActualMonthAndNeedsTheAmountsReconciled()
    {
        var planned = new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc);
        var actual = new DateTime(2026, 11, 30, 9, 0, 0, DateTimeKind.Utc);
        var booking = new Booking { OrgId = Guid.NewGuid(), PropertyId = Guid.NewGuid(), Status = BookingStatus.CheckedOut, CheckedOutAt = actual, CheckOutDate = planned };
        var bookings = new[] { booking }.AsQueryable();
        Assert.Same(booking, Assert.Single(ComoMonthlyDeclaration.DeparturesInMonth(bookings, booking.OrgId, booking.PropertyId, new DateOnly(2026, 11, 1))));
        Assert.Empty(ComoMonthlyDeclaration.DeparturesInMonth(bookings, booking.OrgId, booking.PropertyId, new DateOnly(2026, 12, 1)));
        var report = new ComoMonthlyDeclaration();
        Assert.False(report.CanIncludeDeparture(booking.Status, booking.CheckedOutAt, booking.CheckOutDate));
        Assert.False(report.CanIncludeDeparture(BookingStatus.CheckedIn, null, planned));
        Assert.False(report.CanIncludeDeparture(BookingStatus.CheckedOut, null, planned));
        Assert.True(report.CanIncludeDeparture(BookingStatus.CheckedOut, planned.AddHours(9), planned));
        Assert.Equal(3, report.DeparturesToReview);
        Assert.Equal(0m, report.HostTax);
    }

    [Theory]
    [InlineData(true, "health_care")]
    [InlineData(false, "restricted")]
    public void SeparatesTaxableGuestNightsFromExemptGuestsAndNightsAboveTheCap(bool canReadReasons, string reason)
    {
        var report = new ComoMonthlyDeclaration();
        report.Add(Tax, 2, canReadReasons);
        Assert.Equal(1, report.TaxableGuests);
        Assert.Equal(4, report.TaxableGuestNights);
        Assert.Equal(12m, report.HostTax);
        Assert.Equal(new ComoExemptionCount(1, 2), report.Exemptions["over_four_nights"]);
        Assert.Equal(new ComoExemptionCount(1, 6), report.Exemptions[reason]);
        Assert.Equal(2, report.Exemptions.Count);
    }

    [Fact]
    public void SeparatesExternalCollectionsAndIncompleteStaysFromDeclaredTotals()
    {
        var report = new ComoMonthlyDeclaration();
        report.Add(Tax with { Collector = TouristTaxCollector.Channel }, 2, true);
        report.Add(Tax with { Stale = true }, 2, true);
        report.Add(Tax, 3, true);
        report.Add(Tax with { ConfirmedAt = null }, 2, true);
        Assert.Equal(3, report.UnconfirmedStays);
        Assert.Equal(1, report.ChannelStaysToReconcile);
        Assert.Equal(0, report.HostTax);
        Assert.Empty(report.Exemptions);
    }
}
