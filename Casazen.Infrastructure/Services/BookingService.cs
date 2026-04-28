using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Validation;
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
        // Validate booking data
        var validationResult = BookingValidator.ValidateBooking(booking);
        if (!validationResult.IsValid)
        {
            logger.LogWarning("Booking validation failed: {Errors}", validationResult.ErrorMessage);
            throw new InvalidOperationException($"Booking validation failed: {validationResult.ErrorMessage}");
        }

        // Check property availability
        var isAvailable = await repository.IsAvailableAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate);

        if (!isAvailable)
        {
            logger.LogWarning(
                "Property {PropertyId} not available from {CheckIn} to {CheckOut}",
                booking.PropertyId, booking.CheckInDate, booking.CheckOutDate);
            throw new InvalidOperationException("Property not available for selected dates");
        }

        logger.LogInformation("Creating booking for property {PropertyId}", booking.PropertyId);
        return await repository.AddAsync(booking);
    }

    public async Task<Booking> UpdateBookingAsync(Booking booking)
    {
        // Get existing booking for validation
        var existingBooking = await repository.GetByIdAsync(booking.Id);
        if (existingBooking == null)
            throw new KeyNotFoundException($"Booking {booking.Id} not found");

        // Validate update
        var validationResult = BookingValidator.ValidateBookingUpdate(existingBooking, booking);
        if (!validationResult.IsValid)
        {
            logger.LogWarning("Booking update validation failed: {Errors}", validationResult.ErrorMessage);
            throw new InvalidOperationException($"Booking update validation failed: {validationResult.ErrorMessage}");
        }

        // Validate basic booking data
        var bookingValidation = BookingValidator.ValidateBooking(booking);
        if (!bookingValidation.IsValid)
        {
            logger.LogWarning("Booking validation failed: {Errors}", bookingValidation.ErrorMessage);
            throw new InvalidOperationException($"Booking validation failed: {bookingValidation.ErrorMessage}");
        }

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
