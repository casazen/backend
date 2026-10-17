using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// A guest session just opened (DB-06a): the token to send as <c>Authorization: Bearer {accessToken}</c> to the guest area, until when it
/// works, and the code of the booking the guest proved, formatted (<c>XXXXX-XXXXX</c>). The body of the request is
/// <see cref="GuestBookingLookupRequest"/>: the same proof as "Le mie prenotazioni".
/// </summary>
/// <param name="AccessToken">Opaque; kept for the session only, never in a URL.</param>
/// <param name="ExpiresAt">When the token stops working (UTC). There is no refresh: the guest proves the booking again.</param>
/// <param name="BookingCode">The booking the guest proved, to read it with <c>GET api/public/guest/bookings/{code}</c>.</param>
public sealed record GuestSessionResponse(string AccessToken, DateTime ExpiresAt, string BookingCode)
{
    public static GuestSessionResponse From(GuestSessionOpened opened) =>
        new(opened.AccessToken, opened.ExpiresAt, opened.BookingCode);
}
