using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

public enum RegulatoryAuditAction
{
    Updated = 0,

    MarkedVerified = 1,
}

/// <summary>
/// One admin change to a regulatory reference-data row (territorial agreement, band or IMU channel): who changed what
/// and when, kept forever (never overwritten) so a PEC or a threshold can be traced back to who set it and why
/// (LT-13, A7-22, A7-30). Written by <c>RegulatoryReferenceDataAdminService</c> on every update and verification.
/// </summary>
[Table("RegulatoryDataAuditEntries")]
public class RegulatoryDataAuditEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(100)]
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public RegulatoryAuditAction Action { get; set; }

    [Required, MaxLength(200)]
    public string ChangedByUserId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    [Required]
    public string Changes { get; set; } = string.Empty;
}
