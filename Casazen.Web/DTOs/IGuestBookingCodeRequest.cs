namespace Casazen.Web.DTOs;

/// <summary>
/// The body of an anonymous call that names a booking by its booking code: "Le mie prenotazioni" (<see cref="GuestBookingLookupRequest"/>, BK-11)
/// and the guest session (<see cref="GuestSessionRequest"/>, DB-06a). The limits that count a code (<c>GuestSessionCodeRateLimiter</c>) read it
/// through this interface, whichever of the two bodies the action takes. The code is in the body, never in a URL.
/// </summary>
public interface IGuestBookingCodeRequest
{
    /// <summary>The booking code as the guest typed it (<c>XXXXX-XXXXX</c>, any case, with or without the dash).</summary>
    string BookingCode { get; }
}
