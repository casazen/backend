using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The session of the guest of a booking site, with no account (DB-06a, decision DB-D21). The guest proves one booking with its
/// booking code and the email it was made with (the same check, the same uniform 404 and the same limits as "Le mie prenotazioni",
/// <see cref="IGuestBookingLookupService"/>) and gets a signed token with a short life, to send as <c>Authorization: Bearer</c> to the
/// endpoints of the guest area (<c>api/public/guest/*</c>, policy <c>GuestSession</c>). Nothing is stored: the token is the session. With the flag
/// <c>WhiteLabelHost</c> on the guest may prove the booking with the last four digits of the phone instead of the e-mail (NF-143) and gets the
/// same session.
/// </summary>
/// <remarks>
/// <para><b>For the features that live in the guest area</b> (messages, reviews…): an action protected by the policy
/// <c>GuestSession</c> reads the <see cref="GuestSessionTicket"/> of the request (<c>User.GetGuestSessionTicket()</c>) and asks
/// <see cref="FindBookingAsync"/> for the booking the guest names: it answers the booking, or the uniform
/// <see cref="Exceptions.NotFoundException"/> when the booking is not one of this guest, whatever the reason (another site, another
/// email, a code that does not exist). The request is also scoped to the org of the session (the tenant of the request), so an EF query
/// of a tenant entity cannot reach another org's rows.</para>
/// <para>A token cannot be revoked: "Esci" only forgets it on the device, and a copy works until <see cref="GuestSessionTicket.ExpiresAt"/>.</para>
/// </remarks>
public interface IGuestSessionService
{
    /// <summary>
    /// Opens a session for the booking of <paramref name="credentials"/> (site, code, email). The session is for the guest: every
    /// direct booking of the same site and the same email, which is what the ticket proves.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>, the same whatever does not match.</exception>
    Task<GuestSessionOpened> OpenAsync(GuestBookingCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the same session for the booking of <paramref name="credentials"/> proved with the last four digits of the guest's phone instead of the
    /// e-mail (NF-143; the caller decides that the flag <c>WhiteLabelHost</c> is on). A direct booking of the site whose phone ends with the typed
    /// digits, compared in constant time; the token is the one of the e-mail proof (the hash of the booking's e-mail), so the session is the same.
    /// A code that does not exist, is not a code or is of another site, a booking without a phone and wrong digits take one path and end in the
    /// same <see cref="Exceptions.NotFoundException"/> as a wrong e-mail.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>, the same whatever does not match.</exception>
    Task<GuestSessionOpened> OpenAsync(GuestBookingPhoneCredentials credentials, CancellationToken cancellationToken = default);

    GuestSessionTicket? Validate(string? accessToken);

    Task<GuestBookingView> GetBookingAsync(GuestSessionTicket ticket, string bookingCode, CancellationToken cancellationToken = default);

    Task<Booking> FindBookingAsync(GuestSessionTicket ticket, string bookingCode, CancellationToken cancellationToken = default);
}

/// <summary>What a valid token proves: the org of the booking site and the email of the guest (its hash, never the address).</summary>
/// <param name="OrgId">The org of the site the guest opened the session on.</param>
/// <param name="EmailHash">Hash of the email of the guest, see <see cref="HashEmail"/>.</param>
/// <param name="ExpiresAt">When the session ends (UTC).</param>
public sealed record GuestSessionTicket(Guid OrgId, string EmailHash, DateTime ExpiresAt)
{
    public static string HashEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));
    }

    public bool IsForEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(EmailHash), Encoding.ASCII.GetBytes(HashEmail(email)));
}

public sealed record GuestSessionOpened(string AccessToken, DateTime ExpiresAt, string BookingCode);
