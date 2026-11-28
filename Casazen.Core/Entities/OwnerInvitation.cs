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

    public Guid OrgId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    public virtual PropertyOwner PropertyOwner { get; set; } = null!;

    [Required]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public OwnerInvitationStatus Status { get; set; } = OwnerInvitationStatus.Pending;

    public DateTime ExpiresAt { get; set; }

    public List<DateTime> SendLog { get; set; } = [];

    [Required]
    [MaxLength(255)]
    public string InvitedByUserId { get; set; } = string.Empty;

    public DateTime? AcceptedAt { get; set; }

    public Guid? OwnerUserId { get; set; }

    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
