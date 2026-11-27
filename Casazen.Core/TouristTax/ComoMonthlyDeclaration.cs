using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Core.TouristTax;

/// <summary>Working figures for StayTour, for host-collected stays departing in the month (Como regulation, art. 7).</summary>
public sealed class ComoMonthlyDeclaration
{
    public int TaxableGuests { get; private set; }
    public int TaxableGuestNights { get; private set; }
    public decimal HostTax { get; private set; }
    public int UnconfirmedStays { get; private set; }
    public int DeparturesToReview { get; private set; }
    public int ChannelStaysToReconcile { get; private set; }
    public SortedDictionary<string, ComoExemptionCount> Exemptions { get; } = new(StringComparer.Ordinal);

    public static IQueryable<Booking> DeparturesInMonth(IQueryable<Booking> bookings, Guid orgId, Guid propertyId, DateOnly monthStart)
    {
        var start = RomeCalendar.StartOfDayUtc(monthStart);
        var end = RomeCalendar.StartOfDayUtc(monthStart.AddMonths(1));
        return bookings.Where(b => b.OrgId == orgId && b.PropertyId == propertyId
            && (b.CheckedOutAt ?? b.CheckOutDate) >= start && (b.CheckedOutAt ?? b.CheckOutDate) < end && b.NoShowAt == null
            && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut));
    }

    /// <summary>The confirmed tax uses the booking dates. A changed actual departure requires a corrected tax calculation.</summary>
    public bool CanIncludeDeparture(BookingStatus status, DateTime? actualCheckout, DateTime scheduledCheckout)
    {
        if (status == BookingStatus.CheckedOut && actualCheckout.HasValue &&
            RomeCalendar.DateInRome(actualCheckout.Value) == RomeCalendar.DateInRome(scheduledCheckout))
            return true;
        DeparturesToReview++;
        return false;
    }

    public void Add(StayTouristTaxView tax, int expectedGuests, bool canReadReasons)
    {
        // The OTA's collection date is not recorded by the tax calculator. Its statement must determine the month.
        if (tax.Collector == TouristTaxCollector.Channel)
        {
            ChannelStaysToReconcile++;
            return;
        }
        if (tax.ConfirmedAt is null || tax.Stale || tax.Collector != TouristTaxCollector.Host ||
            tax.Status != TouristTaxQuoteStatus.Calculated || tax.AmountDue is null || expectedGuests < 1 ||
            tax.Guests.Count != expectedGuests || tax.Nights < 1 ||
            tax.Guests.Any(g => g.Nights != tax.Nights || g.TaxableNights < 0 || g.TaxableNights > Math.Min(4, g.Nights)
                || g.Amount is null || (g.ExemptionKind == TouristTaxExemptionKind.None && g.TaxableNights != Math.Min(4, g.Nights))))
        {
            UnconfirmedStays++;
            return;
        }

        HostTax += tax.AmountDue.Value;
        foreach (var guest in tax.Guests)
        {
            if (guest.ExemptionKind != TouristTaxExemptionKind.None)
            {
                var reason = guest.ExemptionKind == TouristTaxExemptionKind.Age
                    ? TouristTaxExemptionReason.MinorAgeCode
                    : canReadReasons ? guest.ExemptionCode ?? "unknown" : "restricted";
                AddExemption(reason, guest.Nights);
                continue;
            }
            TaxableGuests++;
            TaxableGuestNights += guest.TaxableNights;
            if (guest.TaxableNights < guest.Nights)
                AddExemption("over_four_nights", guest.Nights - guest.TaxableNights);
        }
    }

    private void AddExemption(string reason, int nights)
    {
        var previous = Exemptions.GetValueOrDefault(reason);
        Exemptions[reason] = new((previous?.Guests ?? 0) + 1, (previous?.GuestNights ?? 0) + nights);
    }
}

public sealed record ComoExemptionCount(int Guests, int GuestNights);
