using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class BookingService(IBookingRepository repository, ILogger<BookingService> logger) : IBookingService
{
    public async Task<Booking?> GetBookingAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Booking>> GetPropertyBookingsAsync(Guid propertyId)
    {
        return await repository.GetByPropertyAsync(propertyId);
    }

    public async Task<IEnumerable<Booking>> GetGuestBookingsAsync(Guid guestId)
    {
        return await repository.GetByGuestAsync(guestId);
    }

    public async Task<IEnumerable<Booking>> GetAllBookingsAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<Booking> CreateBookingAsync(Booking booking)
    {
        logger.LogInformation("Creating booking for property {PropertyId}", booking.PropertyId);
        return await repository.AddAsync(booking);
    }

    public async Task<Booking> UpdateBookingAsync(Booking booking)
    {
        logger.LogInformation("Updating booking {Id}", booking.Id);
        return await repository.UpdateAsync(booking);
    }

    public async Task<bool> CancelBookingAsync(Guid bookingId)
    {
        var booking = await repository.GetByIdAsync(bookingId);
        if (booking == null)
            return false;

        booking.Status = BookingStatus.Cancelled;
        await repository.UpdateAsync(booking);
        logger.LogInformation("Booking {Id} cancelled", bookingId);
        return true;
    }

    public async Task<bool> IsPropertyAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut)
    {
        return await repository.IsAvailableAsync(propertyId, checkIn, checkOut);
    }

    public async Task<IEnumerable<Booking>> GetCalendarAsync(Guid propertyId, DateTime startDate, DateTime endDate)
    {
        return await repository.GetByDateRangeAsync(propertyId, startDate, endDate);
    }
}
