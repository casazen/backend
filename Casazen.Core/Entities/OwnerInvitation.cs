using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Invitation to link a PropertyOwner to an OwnerUser (ADR-011-R4, ADR-011-R10).
/// The agency invites a property owner by email; the owner accepts and gets portal access.
/// The secret token is never stored - only its SHA-256 hash.
/// </summary>
/// <remarks>
/// Rate limit: 100 invitations/day/org enforced by counting SendLog entries (ADR-011-R4).
/// Idempotent acceptance: duplicate clicks do not fail (ADR-011-R10).
/// </remarks>
[Table("OwnerInvitations")]
public class OwnerInvitation : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The agency org inviting the owner.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The PropertyOwner being invited to get portal access.</summary>
    public Guid PropertyOwnerId { get; set; }

    public virtual PropertyOwner PropertyOwner { get; set; } = null!;

    /// <summary>Invitation email, normalized (trimmed, lowercase).</summary>
    [Required]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>SHA-256 (lowercase hex) of the link token. The token itself is never stored.</summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public OwnerInvitationStatus Status { get; set; } = OwnerInvitationStatus.Pending;

    /// <summary>UTC when the invitation expires.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// The instants (UTC) of the emails sent in the last 24 hours: pruned at each send.
    /// Used for rate limiting (100/day/org, ADR-011-R4).
    /// </summary>
    public List<DateTime> SendLog { get; set; } = [];

    /// <summary>Who invited (User.Id). No foreign key: the history outlives the account.</summary>
    [Required]
    [MaxLength(255)]
    public string InvitedByUserId { get; set; } = string.Empty;

    /// <summary>UTC when the invitation was accepted.</summary>
    public DateTime? AcceptedAt { get; set; }

    /// <summary>The OwnerUser created or linked on acceptance.</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>
    /// UTC when the invitation left Pending status (accepted, revoked, or expired).
    /// Null while pending.
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
