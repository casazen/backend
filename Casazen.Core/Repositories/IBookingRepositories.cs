using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid id);

    /// <summary>
    /// Bookings in the caller's <paramref name="scope"/> (TN-3: the org and, for a non org-wide role, the properties the
    /// caller owns), optionally of one property and/or one guest, with their property and guest, latest check-in first.
    /// One SQL query whatever the number of bookings (A2-17).
    /// </summary>
    Task<IReadOnlyList<Booking>> GetByScopeAsync(
        HostScope scope,
        Guid? propertyId = null,
        Guid? guestId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bookings of the property shown by the host calendar from <paramref name="startDate"/> to <paramref name="endDate"/>
    /// (stay dates, both included: <see cref="Services.HostCalendarRange.BookingShownIn"/>) that take their dates: not cancelled and, when
    /// <paramref name="directPendingTtlMinutes"/> is given, not expired checkout holds
    /// (<see cref="Services.CheckoutHolds.OccupiesDates"/>, BK-21).
    /// </summary>
    Task<IEnumerable<Booking>> GetByDateRangeAsync(
        Guid propertyId,
        DateTime startDate,
        DateTime endDate,
        int? directPendingTtlMinutes = null);

    /// <summary>
    /// True when no booking takes the dates. With <paramref name="directPendingTtlMinutes"/> expired checkout holds are
    /// ignored (<see cref="Services.CheckoutHolds.OccupiesDates"/>); they are cancelled by
    /// <see cref="Services.ICheckoutHoldExpiryService"/>, never here.
    /// </summary>
    Task<bool> IsAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut, int? directPendingTtlMinutes = null);

    Task<Booking> AddAsync(Booking booking);
    Task<Booking> UpdateAsync(Booking booking);
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Removes a direct checkout whose payment could not be started (BK-18, A3-33): the booking, its payment rows and its
    /// guest snapshot, in one transaction under the property lock. The guest is kept when anything else still references
    /// it (another booking, an Alloggiati report). Nothing happens when the booking does not exist.
    /// </summary>
    Task DiscardCheckoutAttemptAsync(Guid bookingId);

    Task<Booking?> GetByExternalIdAsync(Guid propertyId, string externalId, BookingSource source);
    Task<Booking> UpsertOtaBookingAsync(Booking booking);
}