using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid id);

    Task<IReadOnlyList<Booking>> GetByScopeAsync(
        HostScope scope,
        Guid? propertyId = null,
        Guid? guestId = null,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// <see cref="AddAsync(Booking)"/> for the booking of a public checkout that records something of the guest's privacy
    /// history: the optional "send me offers" boxes, one record for each channel the guest ticked (DB-03, E07/F04/US01/T02), and
    /// the soft opt-in notice that was presented. The booking, its guest snapshot and every record
    /// (<see cref="GuestConsentRecord.GuestId"/> is the booking's guest) are written in the same transaction, under the same
    /// property lock and overlap checks. A checkout that loses the dates to a concurrent one leaves no record behind either.
    /// </summary>
    /// <param name="consents">At least one record; each belongs to the guest of the booking.</param>
    Task<Booking> AddWithGuestConsentsAsync(Booking booking, IReadOnlyList<GuestConsentRecord> consents);

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