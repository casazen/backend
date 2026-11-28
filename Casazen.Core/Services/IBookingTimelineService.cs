namespace Casazen.Core.Services;

/// <summary>
/// The timeline of a booking (SR-05a, <c>GET /api/bookings/{id}/timeline</c>): the entries of <see cref="BookingTimeline.Build"/> read
/// from the database in a fixed number of queries (the booking, its payments, refunds, check-in links and Alloggiati report), never one
/// per entry.
/// </summary>
public interface IBookingTimelineService
{
    Task<IReadOnlyList<BookingTimelineEvent>?> GetAsync(
        Guid bookingId,
        bool includeAmounts,
        CancellationToken cancellationToken = default);
}
