using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Biometric verification settings per organization (NF-11, ADR-013-R13).
/// Agency tier defaults Enabled=true, B2C defaults Enabled=false (AC-NF11-14).
/// The thresholds define the three outcome levels: Accept (high confidence),
/// Escalate (uncertain - operator verification), Reject (retry or operator).
/// </summary>
[Table("BiometricSettings")]
public class BiometricSettings : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (ITenantOwned). One row per org.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>Whether biometric verification is enabled for this org (ADR-013-R13).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Similarity threshold above which the comparison is accepted (ADR-013-R4).
    /// Value between 0 and 1 (inclusive).
    /// </summary>
    [Column(TypeName = "decimal(5,4)")]
    public decimal AcceptThreshold { get; set; }

    /// <summary>
    /// Similarity threshold below which the comparison is escalated or rejected (ADR-013-R4).
    /// Value between 0 and 1 (inclusive). Must be less than AcceptThreshold.
    /// Between EscalateThreshold and AcceptThreshold -> Escalate outcome.
    /// At or below EscalateThreshold -> Reject outcome.
    /// </summary>
    [Column(TypeName = "decimal(5,4)")]
    public decimal EscalateThreshold { get; set; }

    /// <summary>Maximum biometric verification attempts before auto-escalation (ADR-013-R5).</summary>
    public int MaxAttempts { get; set; } = 3;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
