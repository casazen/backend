using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>Kind of change recorded on a <see cref="RegulatoryDataAuditEntry"/> (LT-13, A7-22).</summary>
public enum RegulatoryAuditAction
{
    /// <summary>Fields of the row were changed (status, rules, rate, contacts...).</summary>
    Updated = 0,

    /// <summary>The row was marked verified against an official source, without other field changes.</summary>
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

    /// <summary>Kind of row changed: <c>TerritorialRentAgreement</c> or <c>ComuneImuChannel</c>.</summary>
    [Required, MaxLength(100)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Id of the changed row (the agreement's id for a band edit too, so it shows in the agreement's trail).</summary>
    public Guid EntityId { get; set; }

    public RegulatoryAuditAction Action { get; set; }

    [Required, MaxLength(200)]
    public string ChangedByUserId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    /// <summary>Human-readable summary of what changed (field list) or of the verification (date and source).</summary>
    [Required]
    public string Changes { get; set; } = string.Empty;
}
