namespace Casazen.Core.Services;

/// <summary>
/// The timeline of a booking (SR-05a, <c>GET /api/bookings/{id}/timeline</c>): the entries of <see cref="BookingTimeline.Build"/> read
/// from the database in a fixed number of queries (the booking, its payments, refunds, check-in links and Alloggiati report), never one
/// per entry.
/// </summary>
public interface IBookingTimelineService
{
    /// <summary>
    /// The entries of the booking in order, or <c>null</c> when the booking does not exist for the caller's org. The caller has already
    /// checked that it may read the booking (<c>booking.read</c> on its property).
    /// </summary>
    /// <param name="bookingId">The booking.</param>
    /// <param name="includeAmounts">The caller may read payments (<c>payment.read</c>): without it the entries say that money moved, not how much.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<BookingTimelineEvent>?> GetAsync(
        Guid bookingId,
        bool includeAmounts,
        CancellationToken cancellationToken = default);
}
