using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The session of the guest of a booking site, with no account (DB-06a, decision DB-D21). The guest proves one booking with its
/// booking code and the email it was made with (the same check, the same uniform 404 and the same limits as "Le mie prenotazioni",
/// <see cref="IGuestBookingLookupService"/>) and gets a signed token with a short life, to send as <c>Authorization: Bearer</c> to the
/// endpoints of the guest area (<c>api/public/guest/*</c>, policy <c>GuestSession</c>). Nothing is stored: the token is the session.
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

    /// <summary>The ticket in <paramref name="accessToken"/>, or <c>null</c> when it is not a token of ours, was altered or has expired.</summary>
    GuestSessionTicket? Validate(string? accessToken);

    /// <summary>Booking <paramref name="bookingCode"/> as its guest sees it (the view of "Le mie prenotazioni").</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>.</exception>
    Task<GuestBookingView> GetBookingAsync(GuestSessionTicket ticket, string bookingCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// The direct booking <paramref name="bookingCode"/> of the guest of <paramref name="ticket"/> (read-only, with its property, org,
    /// guest and payments), for the services that act on it.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="GuestBookingLookupErrorCodes.NotFound"/>.</exception>
    Task<Booking> FindBookingAsync(GuestSessionTicket ticket, string bookingCode, CancellationToken cancellationToken = default);
}

/// <summary>What a valid token proves: the org of the booking site and the email of the guest (its hash, never the address).</summary>
/// <param name="OrgId">The org of the site the guest opened the session on.</param>
/// <param name="EmailHash">Hash of the email of the guest, see <see cref="HashEmail"/>.</param>
/// <param name="ExpiresAt">When the session ends (UTC).</param>
public sealed record GuestSessionTicket(Guid OrgId, string EmailHash, DateTime ExpiresAt)
{
    /// <summary>The hash of an email as the token carries it: SHA-256 of the address trimmed and lowercase, in upper-case hex.</summary>
    public static string HashEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));
    }

    /// <summary>True when <paramref name="email"/> is the address of the guest of this session (compared in constant time).</summary>
    public bool IsForEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(EmailHash), Encoding.ASCII.GetBytes(HashEmail(email)));
}

/// <summary>A session just opened: the token to send, until when it works, and the booking that was proved, formatted.</summary>
/// <param name="AccessToken">Opaque. Sent as <c>Authorization: Bearer {AccessToken}</c>.</param>
/// <param name="ExpiresAt">When the token stops working (UTC).</param>
/// <param name="BookingCode">The code of the booking the guest proved, <c>XXXXX-XXXXX</c>.</param>
public sealed record GuestSessionOpened(string AccessToken, DateTime ExpiresAt, string BookingCode);
