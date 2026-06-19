using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IBookingService
{
    Task<IEnumerable<Booking>> GetAllBookingsAsync();
    Task<Booking?> GetBookingAsync(Guid id);
    Task<IEnumerable<Booking>> GetPropertyBookingsAsync(Guid propertyId);
    Task<IEnumerable<Booking>> GetGuestBookingsAsync(Guid guestId);
    Task<IEnumerable<Booking>> GetBookingsByEmailAsync(string email);
    Task<Booking> CreateBookingAsync(Booking booking);
    Task<Booking> UpdateBookingAsync(Booking booking);
    Task<bool> CancelBookingAsync(Guid bookingId);
    Task<bool> IsPropertyAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut, int? pendingDirectTtlMinutes = null);
    Task<int> CancelExpiredPendingDirectBookingsAsync(Guid propertyId, int pendingDirectTtlMinutes);
    Task<IEnumerable<Booking>> GetCalendarAsync(Guid propertyId, DateTime startDate, DateTime endDate);
    Task<DirectBookingCreateResult> CreateDirectBookingAsync(DirectBookingCreateInput input);
}