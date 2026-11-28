using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// One admin action on the supplier marketplace: who suspended or reactivated a supplier, who resent or revoked an
/// invite, when and why. Append-only, never overwritten (SU-12, A4-29): a suspension must be traceable to an admin and a
/// reason. Written by <c>SupplierAdminService</c>, in the same transaction as the change it records.
/// </summary>
[Table("SupplierAdminAuditEntries")]
public class SupplierAdminAuditEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public SupplierAdminAuditAction Action { get; set; }

    public Guid? SupplierOrgId { get; set; }

    public Guid? InviteId { get; set; }

    [Required, MaxLength(200)]
    public string ActorUserId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }

    public SupplierStatus? PreviousStatus { get; set; }

    public SupplierStatus? NewStatus { get; set; }
}
