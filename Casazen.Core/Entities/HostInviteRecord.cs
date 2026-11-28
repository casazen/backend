using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Invitation of a prospective <b>independent host</b> (a new host org, not a member of an existing one) sent by a platform
/// super admin or by a host. Staff invites identify founders of new agencies; host referrals use the ordinary private signup.
/// The invite binds a verified email and never grants platform staff access. Onboarding makes founders owners of their own org.
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

    [Required, MaxLength(EmailMaxLength)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    [Required, MaxLength(5)]
    public string Locale { get; set; } = "it";

    [MaxLength(MessageMaxLength)]
    public string? Message { get; set; }

    [Required, MaxLength(255)]
    public string InvitedByUserId { get; set; } = string.Empty;

    public Guid? InvitedByOrgId { get; set; }

    public bool IsUsed { get; set; }

    public DateTime? UsedAt { get; set; }

    [MaxLength(255)]
    public string? UsedByUserId { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PersonalDataErasedAt { get; set; }
}
