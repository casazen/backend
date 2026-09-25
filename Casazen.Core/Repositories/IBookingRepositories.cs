using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid id);
    Task<Booking?> GetByCheckInTokenAsync(Guid checkInToken);
    Task<IEnumerable<Booking>> GetByPropertyAsync(Guid propertyId);
    Task<IEnumerable<Booking>> GetByGuestAsync(Guid guestId);
    Task<IEnumerable<Booking>> GetAllAsync();
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
    Task<Booking?> GetByExternalIdAsync(Guid propertyId, string externalId, BookingSource source);
    Task<Booking> UpsertOtaBookingAsync(Booking booking);
}