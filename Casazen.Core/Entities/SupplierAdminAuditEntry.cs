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

    /// <summary>The supplier org the action is about; null for an invite action (see <see cref="InviteId"/>).</summary>
    public Guid? SupplierOrgId { get; set; }

    /// <summary>The invite an invite action is about.</summary>
    public Guid? InviteId { get; set; }

    /// <summary>Auth0 subject of the admin who acted.</summary>
    [Required, MaxLength(200)]
    public string ActorUserId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    /// <summary>The reason the admin gave (required to suspend); never shown to the supplier.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    /// <summary>Status of the supplier before a suspension or reactivation.</summary>
    public SupplierStatus? PreviousStatus { get; set; }

    /// <summary>Status of the supplier after a suspension or reactivation.</summary>
    public SupplierStatus? NewStatus { get; set; }
}
