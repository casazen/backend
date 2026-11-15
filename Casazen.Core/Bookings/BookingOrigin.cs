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
    /// <summary>A portal of the channel manager (Airbnb, Booking.com, any other channel, <see cref="FiscalCopy.IsOtaBookingSource"/>); the channel is <see cref="BookingOrigins.PortalOf"/>.</summary>
    Portal = 0,

    /// <summary>The site of the agency: a booking of the booking site made from a link of the agency (<c>agency-site</c>), or a reservation of the booking engine of the agency (<c>octorate-booking-engine</c>).</summary>
    AgencySite = 1,

    /// <summary>CasaZen's own site: a booking of the booking site with no attribution of the agency.</summary>
    CasaZenSite = 2,

    /// <summary>Entered by the host (<see cref="BookingSource.Manual"/>, or the legacy <see cref="BookingSource.Local"/>).</summary>
    Manual = 3,
}

/// <summary>
/// The one rule of the origin of a booking (NF-123), and the same rule as a predicate for the list: the function and the predicate are tested against
/// each other, so the badge and the filter of the list never disagree.
/// </summary>
public static class BookingOrigins
{
    /// <summary>
    /// The origin of a booking with this <paramref name="source"/> and this <paramref name="attributionSource"/>. A booking of the booking site
    /// (<see cref="BookingSource.Direct"/>) is <see cref="BookingOrigin.AgencySite"/> when its attribution says so
    /// (<see cref="BookingAttribution.IsAgencySource"/>), else <see cref="BookingOrigin.CasaZenSite"/>; a channel (<see cref="FiscalCopy.IsOtaBookingSource"/>,
    /// the one definition) is <see cref="BookingOrigin.Portal"/>; every other source is the host's own record, <see cref="BookingOrigin.Manual"/>.
    /// </summary>
    public static BookingOrigin Of(BookingSource source, string? attributionSource)
    {
        if (source == BookingSource.Direct)
            return BookingAttribution.IsAgencySource(attributionSource) ? BookingOrigin.AgencySite : BookingOrigin.CasaZenSite;

        return FiscalCopy.IsOtaBookingSource(source) ? BookingOrigin.Portal : BookingOrigin.Manual;
    }

    /// <summary>The origin of <paramref name="booking"/> (see <see cref="Of(BookingSource, string?)"/>).</summary>
    public static BookingOrigin Of(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return Of(booking.Source, booking.AttributionSource);
    }

    /// <summary>
    /// The channel of a booking whose origin is <see cref="BookingOrigin.Portal"/>: the name of its <see cref="BookingSource"/> (<c>Airbnb</c>,
    /// <c>BookingCom</c>, <c>OtherChannel</c>, ... where the name of the portal is <see cref="Booking.ChannelLabel"/>); <c>null</c> for any other origin.
    /// </summary>
    public static string? PortalOf(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return Of(booking) == BookingOrigin.Portal ? booking.Source.ToString() : null;
    }

    /// <summary>
    /// The bookings that have any of <paramref name="origins"/>, as a predicate the database can run (<see cref="Of(BookingSource, string?)"/> as a query);
    /// none is no booking at all. The sources are read from the definitions of the rule (<see cref="FiscalCopy.IsOtaBookingSource"/>,
    /// <see cref="BookingAttribution.AgencySources"/>), never copied here.
    /// </summary>
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
