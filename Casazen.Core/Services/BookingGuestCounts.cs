using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public static class BookingGuestCounts
{
    public static (int Adults, int Children) Of(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return booking.NumberOfAdults > 0 && booking.NumberOfAdults + booking.NumberOfChildren == booking.NumberOfGuests
            ? (booking.NumberOfAdults, booking.NumberOfChildren)
            : (booking.NumberOfGuests, 0);
    }
}
