using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The personal stay link of a booking (E50/F01/US01/T01, NF-152, D1, D2, decision A2): the address <c>/s/{token}</c> that opens the guest portal of
/// the stay, for a booking of any source (direct or from a portal). A booking has one active link at a time; a revoked link stays in the table, so
/// that its token stops working for good and is never given to another stay.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The token is the address.</b> <see cref="Token"/> is random (192 bits, base64url, so 32 characters), unique in the whole table (revoked
/// links included) and carries nothing about the stay: it is not derived from an id and cannot be guessed. It is kept as it is, like the token of
/// <see cref="PropertyWelcomeLink"/>, because the host copies the link from the booking.</item>
/// <item><b>One active link for each booking</b>: the partial unique index on <see cref="BookingId"/> where <see cref="RevokedAt"/> is <c>NULL</c>.
/// Regenerating the link is revoking the active one and creating the new one.</item>
/// <item><b>Validity</b>: from its creation to <see cref="ExpiresAt"/>, the end of the 30th day after the departure
/// (<c>GuestPortalRules.ExpiryFor</c>); a later departure moves it on, an earlier one never shortens it.</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the booking, copied when the link is created (TN-2). The rows
/// follow their booking (cascade).</item>
/// </list>
/// </remarks>
[Table("GuestPortalLinks")]
public class GuestPortalLink : ITenantOwned
{
    /// <summary>Random bytes of a <see cref="Token"/>: 192 bits.</summary>
    public const int TokenBytes = 24;

    /// <summary>Column length of <see cref="Token"/>: room beyond the 32 characters of a base64url token of <see cref="TokenBytes"/> bytes.</summary>
    public const int TokenMaxLength = 64;

    /// <summary>Column length of <see cref="CreatedByUserId"/> and <see cref="AccountUserId"/>: the one of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the booking when the link is created (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    /// <summary>The random token in the address of the stay link (base64url, unique).</summary>
    [Required, MaxLength(TokenMaxLength)]
    public string Token { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant the link stops opening: the end of the 30th day after the departure of the stay.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>UTC instant the link was revoked; <c>null</c> = the link is the active one of its booking.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>The person who created the link (<c>User.Id</c>, the Auth0 subject); <c>null</c> when the system did.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// The guest account (<c>User.Id</c>) the stay was added to from the link («Aggiungi al mio account», D1); <c>null</c> while no account holds it.
    /// Written by the guest account task of the portal (E50/F01/US02), which has no migration of its own.
    /// </summary>
    [MaxLength(UserIdMaxLength)]
    public string? AccountUserId { get; set; }

    /// <summary>UTC instant the stay was added to <see cref="AccountUserId"/>; <c>null</c> while no account holds it.</summary>
    public DateTime? AccountAttachedAt { get; set; }
}
