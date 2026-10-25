using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Audit log of biometric verification attempts (NF-11, ADR-013-R3).
/// Records the outcome, attempts, model metadata, and thresholds used.
/// NEVER stores images, embeddings, or PII per ADR-013-R2 (immediate deletion guarantee).
/// If manually verified, records the operator who decided and their note.
/// </summary>
[Table("BiometricVerificationLogs")]
public class BiometricVerificationLog : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (ITenantOwned). Inherited from the booking's property.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    [ForeignKey("Guest")]
    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    /// <summary>
    /// Final outcome of the verification (ADR-013-R4):
    /// Accept (similarity >= AcceptThreshold),
    /// Escalate (EscalateThreshold < similarity < AcceptThreshold),
    /// Reject (similarity <= EscalateThreshold).
    /// </summary>
    public BiometricOutcome Outcome { get; set; }

    /// <summary>Number of biometric attempts made before reaching the final outcome.</summary>
    public int Attempts { get; set; }

    /// <summary>Name of the face recognition model used (e.g., "AuraFace-v1", "SFace").</summary>
    [Required, MaxLength(100)]
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Version of the model used (e.g., "1.0.0"). For audit trail and model provenance (ADR-013-R14).</summary>
    [Required, MaxLength(50)]
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>
    /// JSON snapshot of the thresholds used at verification time (AcceptThreshold, EscalateThreshold).
    /// Captures the configuration that produced this outcome, even if settings change later.
    /// Example: {"AcceptThreshold": 0.85, "EscalateThreshold": 0.70}
    /// </summary>
    [Required]
    [Column(TypeName = "jsonb")]
    public string ThresholdsJson { get; set; } = "{}";

    /// <summary>
    /// If the verification was manually reviewed by an operator, the user ID of that operator.
    /// Null for fully automated outcomes (Accept, Reject without operator intervention).
    /// </summary>
    [MaxLength(255)]
    [ForeignKey("OperatorUser")]
    public string? OperatorUserId { get; set; }
    public virtual User? OperatorUser { get; set; }

    /// <summary>
    /// Free-text note from the operator if manually verified (why accepted/rejected).
    /// Null for automated outcomes.
    /// </summary>
    public string? OperatorNote { get; set; }

    /// <summary>When this log entry was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
