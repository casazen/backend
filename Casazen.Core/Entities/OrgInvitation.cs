using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// An invitation to join an org with a role (AM-02): the owner or an administrator invites a person by email, the person
/// accepts with the account that has that email and becomes an <see cref="OrgMember"/>
/// (<c>IOrgInvitationService</c>, runbook <c>docs/runbooks/org-team.md</c>).
/// </summary>
/// <remarks>
/// <para><b>The secret is never stored.</b> The link carries a random 256-bit token
/// (<see cref="Casazen.Core.OrgTeam.OrgInvitationTokens"/>); the row keeps only its SHA-256 (<see cref="TokenHash"/>).
/// Sending the invitation again, copying its link and the reminder of the third day all rotate the token: the hash is
/// replaced and the previous link stops working.</para>
/// <para><b>Personal data.</b> <see cref="Email"/> and <see cref="Name"/> of the invitee live here and nowhere else: they
/// are deleted <c>OrgTeam:InvitationRetentionDays</c> (30) after the invitation was closed, together with the row
/// (<c>IOrgInvitationMaintenanceService</c>). The member keeps only what its <see cref="User"/> row holds.</para>
/// <para><b>Seats.</b> A pending invitation that has not expired holds one seat of the plan, like an active member
/// (<c>IOrgSeatService</c>). At most one pending invitation per org and email (partial unique index).</para>
/// </remarks>
[Table("OrgInvitations")]
public class OrgInvitation : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The org the person is invited to.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The invited email, normalized (trimmed, lowercase): the accepting account must have this very address.</summary>
    [Required, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>The invitee's name as the inviter wrote it (shown in the team page and in the email).</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The org role the person gets. Never <see cref="OrgRole.Owner"/> (D15).</summary>
    public OrgRole Role { get; set; }

    /// <summary>The areas (rental contexts: <c>short-rent</c>, <c>long-rent</c>) the person will work in.</summary>
    public List<string> Areas { get; set; } = [];

    /// <summary>
    /// Which properties the person will reach. The list of properties for <see cref="PropertyScope.Selected"/> arrives with
    /// AM-03; here the value is only recorded and copied to the member.
    /// </summary>
    public PropertyScope PropertyScope { get; set; } = PropertyScope.All;

    /// <summary>SHA-256 (lowercase hex) of the link token; unique. The token itself is never stored.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public OrgInvitationStatus Status { get; set; } = OrgInvitationStatus.Pending;

    /// <summary>UTC. From the moment the invitation is sent (or sent again) <see cref="Casazen.Core.OrgTeam.OrgInvitationRules.Validity"/> later.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>UTC when the reminder of the third day was sent; <c>null</c> until then (and again after the invitation is sent again).</summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>Who invited (<c>User.Id</c>). No foreign key: the history outlives the account.</summary>
    [Required, MaxLength(255)]
    public string InvitedByUserId { get; set; } = string.Empty;

    /// <summary>Language of the emails of this invitation: <c>it</c> or <c>en</c>.</summary>
    [Required, MaxLength(8)]
    public string Language { get; set; } = "it";

    public DateTime? AcceptedAt { get; set; }

    /// <summary>The account that accepted (<c>User.Id</c>).</summary>
    [MaxLength(255)]
    public string? AcceptedByUserId { get; set; }

    /// <summary>
    /// UTC when the invitation left <see cref="OrgInvitationStatus.Pending"/> (accepted, revoked, or its expiry): the retention
    /// of the personal data counts from here. <c>null</c> while pending.
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
