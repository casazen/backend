using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Validation;

public static class BookingValidator
{
    public static BookingValidationResult ValidateBooking(
        Booking booking,
        bool allowPastCheckIn = false,
        DateTime? today = null)
    {
        var errors = new List<string>();
        var referenceToday = today ?? TimeProvider.System.TodayInRome();

        if (!allowPastCheckIn && booking.CheckInDate.Date < referenceToday)
        {
            errors.Add("Check-in date cannot be in the past");
        }

        if (booking.CheckOutDate <= booking.CheckInDate)
        {
            errors.Add("Check-out date must be after check-in date");
        }

        if (booking.TotalPrice < 0)
        {
            errors.Add("Total price cannot be negative");
        }

        if (booking.NumberOfGuests <= 0)
        {
            errors.Add("Number of guests must be at least 1");
        }

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

        if (existingBooking.Status == BookingStatus.CheckedIn ||
            existingBooking.Status == BookingStatus.CheckedOut)
        {
            if (updates.CheckInDate != existingBooking.CheckInDate ||
                updates.CheckOutDate != existingBooking.CheckOutDate)
            {
                errors.Add("Cannot change dates for bookings that are already checked in or completed");
            }
        }

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
