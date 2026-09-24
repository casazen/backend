using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid id);
    Task<Booking?> GetByCheckInTokenAsync(Guid checkInToken);
    Task<IEnumerable<Booking>> GetByPropertyAsync(Guid propertyId);
    Task<IEnumerable<Booking>> GetByGuestAsync(Guid guestId);
    Task<IEnumerable<Booking>> GetAllAsync();
    Task<IEnumerable<Booking>> GetByDateRangeAsync(Guid propertyId, DateTime startDate, DateTime endDate);
    Task<bool> IsAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut, int? directPendingTtlMinutes = null);

    /// <summary>
    /// Cancels the abandoned holds of the public checkout (<see cref="BookingSource.Direct"/>, still
    /// <see cref="BookingStatus.Pending"/>, with a Stripe PaymentIntent or SetupIntent, created more than
    /// <paramref name="ttlMinutes"/> ago) whose stay overlaps [<paramref name="checkIn"/>, <paramref name="checkOut"/>).
    /// Host bookings (<see cref="BookingSource.Manual"/>) and holds of other dates are never touched (PC-01, A2-01).
    /// </summary>
    Task<int> CancelExpiredPendingDirectBookingsAsync(Guid propertyId, DateTime checkIn, DateTime checkOut, int ttlMinutes);

    Task<Booking> AddAsync(Booking booking);
    Task<Booking> UpdateAsync(Booking booking);
    Task DeleteAsync(Guid id);
    Task<Booking?> GetByExternalIdAsync(Guid propertyId, string externalId, BookingSource source);
    Task<Booking> UpsertOtaBookingAsync(Booking booking);
}