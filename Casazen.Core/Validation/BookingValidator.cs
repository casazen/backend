using Casazen.Core.Entities;

namespace Casazen.Core.Validation;

public static class BookingValidator
{
    public static BookingValidationResult ValidateBooking(Booking booking)
    {
        var errors = new List<string>();

        // Validate dates
        if (booking.CheckInDate < DateTime.UtcNow.Date)
        {
            errors.Add("Check-in date cannot be in the past");
        }

        if (booking.CheckOutDate <= booking.CheckInDate)
        {
            errors.Add("Check-out date must be after check-in date");
        }

        // Validate price
        if (booking.TotalPrice < 0)
        {
            errors.Add("Total price cannot be negative");
        }

        // Validate number of guests
        if (booking.NumberOfGuests <= 0)
        {
            errors.Add("Number of guests must be at least 1");
        }

        // Validate IDs
        if (booking.PropertyId == Guid.Empty)
        {
            errors.Add("Property ID is required");
        }

        if (booking.GuestId == Guid.Empty)
        {
            errors.Add("Guest ID is required");
        }

        return new BookingValidationResult(errors.Count == 0, errors);
    }

    public static BookingValidationResult ValidateBookingUpdate(Booking existingBooking, Booking updates)
    {
        var errors = new List<string>();

        // Cannot change booking dates if already checked in or completed
        if (existingBooking.Status == BookingStatus.CheckedIn ||
            existingBooking.Status == BookingStatus.CheckedOut)
        {
            if (updates.CheckInDate != existingBooking.CheckInDate ||
                updates.CheckOutDate != existingBooking.CheckOutDate)
            {
                errors.Add("Cannot change dates for bookings that are already checked in or completed");
            }
        }

        // Cannot modify cancelled bookings
        if (existingBooking.Status == BookingStatus.Cancelled)
        {
            errors.Add("Cannot modify cancelled bookings");
        }

        return new BookingValidationResult(errors.Count == 0, errors);
    }
}

public record BookingValidationResult(bool IsValid, List<string> Errors)
{
    public string ErrorMessage => string.Join("; ", Errors);
}
