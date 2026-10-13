using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>Adults and minors of a booking (PC-07).</summary>
public static class BookingGuestCounts
{
    /// <summary>
    /// Adults and minors as stored. A booking whose split does not add up to <see cref="Booking.NumberOfGuests"/>, such
    /// as the host bookings made before the form asked for minors (both 0), counts every guest as an adult, as the tourist
    /// tax of those bookings did.
    /// </summary>
    public static (int Adults, int Children) Of(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return booking.NumberOfAdults > 0 && booking.NumberOfAdults + booking.NumberOfChildren == booking.NumberOfGuests
            ? (booking.NumberOfAdults, booking.NumberOfChildren)
            : (booking.NumberOfGuests, 0);
    }
}
