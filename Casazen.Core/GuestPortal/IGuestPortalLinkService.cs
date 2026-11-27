using Casazen.Core.Entities;

namespace Casazen.Core.GuestPortal;

/// <summary>
/// The stay link of a booking (E50/F01/US01/T01, NF-152, D1, D2, decision A2): the address <c>/s/{token}</c> of the guest portal of the stay. A booking
/// has <b>one active link</b> at a time, created <b>on request</b> (no recovery job) for a booking of any source the rules let in
/// (<see cref="GuestPortalRules"/>). The token is 192 random bits in base64url, carries nothing about the stay and is unique in the whole table, so a
/// link never reaches another stay. Regenerating revokes the active link and makes the new one in one transaction: the old token answers like a
/// token that never existed.
/// </summary>
/// <remarks>
/// Every host method names the org, which the web layer took from the booking the caller may read or write (tenant and HostScope): services never
/// check roles. A booking that is not in the org is a <see cref="Exceptions.NotFoundException"/>. The logs carry the id of the link, never its token.
/// </remarks>
public interface IGuestPortalLinkService
{
    /// <summary>The active link of the booking as it is stored (expired or not), or <c>null</c> when it has none.</summary>
    Task<GuestPortalLink?> GetActiveAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active link of the booking; when there is none, a new one is made. <c>null</c> when the booking may not have one
    /// (<see cref="GuestPortalRules.IneligibleReason(Booking, DateTime)"/>). Asked twice, the same link: two callers racing to make the first link end
    /// with one (the database refuses the second row and the second caller gets the first one). A later departure extends the link it returns.
    /// </summary>
    /// <param name="createdByUserId">The user (Auth0 subject) who asked, <c>null</c> when the system did.</param>
    Task<GuestPortalLinkResult?> GetOrCreateAsync(
        Guid orgId,
        Guid bookingId,
        string? createdByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the active link, if any, and makes a new one with a new token, in one transaction; <c>null</c> (and nothing revoked) when the booking
    /// may not have a link. The new link is the only active one of the booking.
    /// </summary>
    Task<GuestPortalLink?> RegenerateAsync(
        Guid orgId,
        Guid bookingId,
        string? createdByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes the active link of the booking: its token stops opening at once. <c>false</c> when there was none.</summary>
    Task<bool> RevokeAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active link a token opens, or <c>null</c> for a token that is unknown, revoked, expired, malformed, of a booking that is only a request or of
    /// an org that is not active: all the same, so the answer never tells which. A well-formed token costs one query; a departure moved later
    /// extends the stored expiry (never shortens it). The token alone decides, whoever calls: the tenant of the request is not read.
    /// </summary>
    Task<GuestPortalLink?> ResolveAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>The address of the link on the CasaZen domain: <c>App:PublicSiteBaseUrl</c> + <c>/s/</c> + token.</summary>
    string BuildUrl(GuestPortalLink link);
}

/// <summary>An active stay link and whether the call just made it.</summary>
/// <param name="Link">The active link.</param>
/// <param name="Created"><c>true</c> when this call made it, <c>false</c> when the booking already had it.</param>
public sealed record GuestPortalLinkResult(GuestPortalLink Link, bool Created);
