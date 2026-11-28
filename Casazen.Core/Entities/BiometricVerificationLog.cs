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
    /// <summary>
    /// Name of the unique index on (<see cref="BookingId"/>, <see cref="IdempotencyKey"/>), filtered on a key: the last line of defence of the idempotent closure
    /// (E70/F01/US01/T02, NF-194): two processes that close the same attempt write one row.
    /// </summary>
    public const string IdempotencyIndexName = "UIX_BiometricVerificationLogs_BookingId_IdempotencyKey";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    [ForeignKey("Guest")]
    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    public BiometricOutcome Outcome { get; set; }

    public int Attempts { get; set; }

    [Required, MaxLength(100)]
    public string ModelName { get; set; } = string.Empty;

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

    [MaxLength(255)]
    [ForeignKey("OperatorUser")]
    public string? OperatorUserId { get; set; }
    public virtual User? OperatorUser { get; set; }

    public string? OperatorNote { get; set; }

    public VerificationMethod Method { get; set; } = VerificationMethod.Biometric;

    /// <summary>
    /// The checks of the decision, a JSON array of <c>{ check, result, value?, threshold? }</c> (<c>VerificationReasons</c>, AC-NF51-8): codes,
    /// numbers and thresholds only, never an image, a vector, a name or a document number. <c>[]</c> for the rows written before the column. An
    /// operator row repeats the reasons of the automatic outcome it replaces.
    /// </summary>
    [Required]
    [Column(TypeName = "jsonb")]
    public string ReasonsJson { get; set; } = "[]";

    public bool Provisional { get; set; }

    public int? DurationMs { get; set; }

    [MaxLength(50)]
    public string? PolicyVersion { get; set; }

    [MaxLength(64)]
    public string? PolicyHash { get; set; }

    public IdentityDecisionMode DecisionMode { get; set; } = IdentityDecisionMode.Legacy;

    public BiometricOutcome? ShadowOutcome { get; set; }

    [MaxLength(40)]
    public string? FormatId { get; set; }

    [MaxLength(40)]
    public string? Channel { get; set; }

    /// <summary>
    /// The idempotency key of the attempt the row closes (<c>IdentityHeaders.IdempotencyKey</c>); unique per booking (<see cref="IdempotencyIndexName"/>); null on
    /// the legacy path.
    /// </summary>
    [MaxLength(64)]
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
