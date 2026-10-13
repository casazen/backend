using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Invitation of a prospective <b>independent host</b> (a new host org, not a member of an existing one) sent by a platform
/// admin or by a host. The invite grants no role and no privilege: the person signs up with the normal self-service flow;
/// the invite only binds the email, leads to the signup page and records who invited (attribution).
/// </summary>
/// <remarks>
/// <para><b>The secret is never stored.</b> The link carries a random token
/// (<see cref="Casazen.Core.Suppliers.SupplierInviteTokens"/>, the same scheme as the supplier invites); the row keeps only its
/// SHA-256 (<see cref="TokenHash"/>). Sending the invite again replaces the hash: the old link stops working.</para>
/// <para><b>Personal data.</b> <see cref="Email"/> and <see cref="Message"/> are erased
/// <c>HostInvites:RetentionDays</c> after the invite was closed (<c>IHostInviteRetentionService</c>): a closed invite that
/// nobody used is deleted, a used one is kept for attribution with its email replaced (<see cref="PersonalDataErasedAt"/>).</para>
/// <para><b>Tenancy.</b> Not <c>ITenantOwned</c>: an admin's invite has no org, and the invitee has no org yet.
/// <see cref="InvitedByOrgId"/> is the attribution (and the scope of the host quota), never a tenant filter; the host
/// endpoints always filter by the caller's own <see cref="InvitedByUserId"/>.</para>
/// </remarks>
[Table("HostInviteRecords")]
public class HostInviteRecord
{
    public const int EmailMaxLength = 255;
    public const int MessageMaxLength = 500;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The invited email, normalized (trimmed, lowercase): the accepting account must have this very address.</summary>
    [Required, MaxLength(EmailMaxLength)]
    public string Email { get; set; } = string.Empty;

    /// <summary>SHA-256 (lowercase hex) of the link token; unique. The token itself is never stored.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Language of the invite email (<c>it</c> or <c>en</c>).</summary>
    [Required, MaxLength(5)]
    public string Locale { get; set; } = "it";

    /// <summary>Optional note of the inviter, shown in the email (HTML-encoded).</summary>
    [MaxLength(MessageMaxLength)]
    public string? Message { get; set; }

    /// <summary>The account (<c>Users.Id</c>, Auth0 subject) that created the invite: an admin or a host.</summary>
    [Required, MaxLength(255)]
    public string InvitedByUserId { get; set; } = string.Empty;

    /// <summary>The host org of the inviter; null when a platform admin invited.</summary>
    public Guid? InvitedByOrgId { get; set; }

    public bool IsUsed { get; set; }

    /// <summary>UTC moment the invite was accepted.</summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>The account that accepted the invite.</summary>
    [MaxLength(255)]
    public string? UsedByUserId { get; set; }

    /// <summary>UTC moment the invite was revoked: its link no longer works. The row is kept for the lists and the audit.</summary>
    public DateTime? RevokedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC moment <see cref="Email"/> and <see cref="Message"/> were erased after the retention (a used invite only).</summary>
    public DateTime? PersonalDataErasedAt { get; set; }
}
