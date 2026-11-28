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
    public const int TokenBytes = 24;

    public const int TokenMaxLength = 64;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    [Required, MaxLength(TokenMaxLength)]
    public string Token { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? AccountUserId { get; set; }

    public DateTime? AccountAttachedAt { get; set; }
}
