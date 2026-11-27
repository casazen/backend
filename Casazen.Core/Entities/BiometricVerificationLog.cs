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

    /// <summary>
    /// Who decided (E03/F04/US02/T01): the verification agent (<see cref="VerificationMethod.Biometric"/>, the default of the rows written before
    /// the column) or an operator from the queue (<see cref="VerificationMethod.Operator"/>, with <see cref="OperatorUserId"/> and <see cref="OperatorNote"/>).
    /// </summary>
    public VerificationMethod Method { get; set; } = VerificationMethod.Biometric;

    /// <summary>
    /// The checks of the decision, a JSON array of <c>{ check, result, value?, threshold? }</c> (<c>VerificationReasons</c>, AC-NF51-8): codes,
    /// numbers and thresholds only, never an image, a vector, a name or a document number. <c>[]</c> for the rows written before the column. An
    /// operator row repeats the reasons of the automatic outcome it replaces.
    /// </summary>
    [Required]
    [Column(TypeName = "jsonb")]
    public string ReasonsJson { get; set; } = "[]";

    /// <summary>
    /// The thresholds in force were not calibrated when the decision was taken (<c>BiometricSettings.CalibratedAt</c> was null): the operator
    /// console says the outcome rests on provisional thresholds (AC-NF51 point 7).
    /// </summary>
    public bool Provisional { get; set; }

    /// <summary>How long the automatic verification took, in milliseconds; null for an operator decision and for the rows written before the column.</summary>
    public int? DurationMs { get; set; }

    // E70/F01/US01/T02 (NF-194, ADR-013-R18, R19): how the self-hosted policy decided. A row of the legacy path, and every row written before the columns, has no
    // policy, the mode Legacy, no shadow outcome and no key. Codes and hashes only.

    /// <summary>The version of the policy file that decided; null on the legacy path.</summary>
    [MaxLength(50)]
    public string? PolicyVersion { get; set; }

    /// <summary>The canonical SHA-256 (hex) of that policy file; null on the legacy path.</summary>
    [MaxLength(64)]
    public string? PolicyHash { get; set; }

    /// <summary>
    /// How the decision was taken: <see cref="IdentityDecisionMode.Legacy"/> (the default, and every row written before the column), by the policy in observation,
    /// or by the policy in a qualified segment.
    /// </summary>
    public IdentityDecisionMode DecisionMode { get; set; } = IdentityDecisionMode.Legacy;

    /// <summary>The decision of the policy recorded as the shadow decision while a person decides (ADR-013-R19); null on the legacy path.</summary>
    public BiometricOutcome? ShadowOutcome { get; set; }

    /// <summary>The id of the format of the registry the document was read as.</summary>
    [MaxLength(40)]
    public string? FormatId { get; set; }

    /// <summary>The coarse family of the browser, a code of <c>IdentityChannels</c>.</summary>
    [MaxLength(40)]
    public string? Channel { get; set; }

    /// <summary>
    /// The idempotency key of the attempt the row closes (<c>IdentityHeaders.IdempotencyKey</c>); unique per booking (<see cref="IdempotencyIndexName"/>); null on
    /// the legacy path.
    /// </summary>
    [MaxLength(64)]
    public string? IdempotencyKey { get; set; }

    /// <summary>When this log entry was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
