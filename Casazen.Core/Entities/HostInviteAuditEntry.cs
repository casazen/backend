using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// One action on a host invite: who created, sent again or revoked it, for which org, and when. Append-only, written in the
/// same transaction as the change it records. It holds no email: it outlives the invite (and the erasure of its personal
/// data), and the daily quota of a host org is counted from it. Same pattern as <see cref="SupplierAdminAuditEntry"/>.
/// </summary>
[Table("HostInviteAuditEntries")]
public class HostInviteAuditEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The invite the action is about (no foreign key: the trail outlives a purged invite).</summary>
    public Guid InviteId { get; set; }

    public HostInviteAuditAction Action { get; set; }

    /// <summary>The account that acted.</summary>
    [Required, MaxLength(255)]
    public string ActorUserId { get; set; } = string.Empty;

    /// <summary>The host org of the actor; null for a platform admin (no quota).</summary>
    public Guid? ActorOrgId { get; set; }

    public DateTime OccurredAt { get; set; }
}
