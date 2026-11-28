using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Core.Bookings;

/// <summary>
/// Where a booking came from, in the four words the host reads (E27/F01/US01/T01, NF-123): a portal, the site of the agency, CasaZen's own site or
/// the host. <see cref="BookingSource"/> is unchanged (the invoicing plan reasons per channel): the origin is worked out from the source and from the
/// attribution of the booking (<see cref="BookingOrigins.Of(BookingSource, string?)"/>), never stored. Serialized by name: never rename a member the
/// frontend reads.
/// </summary>
public enum BookingOrigin
{
    Portal = 0,

    AgencySite = 1,

    CasaZenSite = 2,

    Manual = 3,
}

public static class BookingOrigins
{
    public static BookingOrigin Of(BookingSource source, string? attributionSource)
    {
        if (source == BookingSource.Direct)
            return BookingAttribution.IsAgencySource(attributionSource) ? BookingOrigin.AgencySite : BookingOrigin.CasaZenSite;

        return FiscalCopy.IsOtaBookingSource(source) ? BookingOrigin.Portal : BookingOrigin.Manual;
    }

    public static BookingOrigin Of(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return Of(booking.Source, booking.AttributionSource);
    }

    public static string? PortalOf(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return Of(booking) == BookingOrigin.Portal ? booking.Source.ToString() : null;
    }

    public static Expression<Func<Booking, bool>> Matching(IEnumerable<BookingOrigin> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        var portals = Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).ToList();
        var agency = BookingAttribution.AgencySources.ToList();
        var predicates = new List<Expression<Func<Booking, bool>>>();
        foreach (var origin in origins.Distinct())
        {
            predicates.Add(origin switch
            {
                BookingOrigin.Portal => b => portals.Contains(b.Source),
                BookingOrigin.AgencySite => b => b.Source == BookingSource.Direct && b.AttributionSource != null && agency.Contains(b.AttributionSource),
                BookingOrigin.CasaZenSite => b => b.Source == BookingSource.Direct && (b.AttributionSource == null || !agency.Contains(b.AttributionSource)),
                _ => b => b.Source != BookingSource.Direct && !portals.Contains(b.Source),
            });
        }

        return BookingListRules.AnyOf(predicates);
    }
}
