using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IBookingService
{
    Task<Booking?> GetBookingAsync(Guid id);
    Task<IEnumerable<Booking>> GetPropertyBookingsAsync(Guid propertyId);
    Task<IEnumerable<Booking>> GetGuestBookingsAsync(Guid guestId);
    Task<Booking> CreateBookingAsync(Booking booking);
    Task<Booking> UpdateBookingAsync(Booking booking);
    Task<bool> CancelBookingAsync(Guid bookingId);
    Task<bool> IsPropertyAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut);
    Task<IEnumerable<Booking>> GetCalendarAsync(Guid propertyId, DateTime startDate, DateTime endDate);
}