using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("GuestCheckInSessions")]
public class GuestCheckInSession : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public Guid OrgId { get; set; }

    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public GuestCheckInSessionStatus Status { get; set; } = GuestCheckInSessionStatus.Inviato;

    public DateTime? SentAt { get; set; }

    public GuestCheckInLinkEmailStatus? LinkEmailStatus { get; set; }

    [MaxLength(50)]
    public string? LinkEmailError { get; set; }

    public DateTime? CompletedAt { get; set; }

    [MaxLength(64)]
    public string? AcceptedHouseRulesVersion { get; set; }

    public int BiometricAttempts { get; set; } = 0;

    public BiometricOutcome? BiometricOutcome { get; set; }

    public DateTime? BiometricConsentAt { get; set; }

    public string? BiometricConsentVersion { get; set; }

    /// <summary>
    /// The SHA-256 (hex, lower case) of the reference text, the Italian file, of the version of the registry the guest accepted (E22/F01/US02/T02,
    /// <see cref="BiometricConsentVersion"/>): with the version it tells which text the consent was given to (GDPR art. 7(1)). Null for a consent given before the
    /// registry existed, and while there is no consent.
    /// </summary>
    [MaxLength(64)]
    public string? BiometricConsentTextHash { get; set; }

    public DateTime? BiometricConsentRefusedAt { get; set; }

    /// <summary>
    /// When the verification was handed to an operator: the guest asked for one, refused the consent, the automatic verification asked for a
    /// review, used its attempts, failed for a technical reason, or was not offered (GDPR art. 22, AC-NF51-11). Null otherwise.
    /// </summary>
    public DateTime? OperatorRequestedAt { get; set; }

    /// <summary>
    /// The checks of the identity document the guest uploaded (E03/F04/US02/T04), as JSON: whether its MRZ was read, its check digits, its expiry,
    /// whether the MRZ agrees with the check-in data field by field, whether its holder is a minor and the SHA-256 of the picture (to recognise it
    /// when it is sent again with the selfie). For a document of two sides (E22/F01/US01/T02) also what each picture showed (a face found, a good
    /// capture, the checks of the MRZ read on it) and the SHA-256 of the back. Flags and hashes only: never a value read on the document. Null before
    /// the first upload.
    /// </summary>
    public string? DocumentCheckJson { get; set; }

    public string? Language { get; set; }

    public DateTime? ReminderSentAt { get; set; }

    // E70/F01/US01/T02 (NF-188, NF-194, NF-196): the self-hosted identity verification of the session (Features:CheckInIdentitySelfHosted). Null, and 0 for the
    // retries, on the legacy path and on every row written before the columns. Ids, codes, hashes and times only: never a value read on a document, a frame or a
    // face datum.

    public Guid? IdentityGuestId { get; set; }

    public DateTime? IdentityOpenedAt { get; set; }

    [MaxLength(50)]
    public string? IdentityNoticeVersion { get; set; }

    [MaxLength(64)]
    public string? IdentityNoticeTextHash { get; set; }

    [MaxLength(20)]
    public string? IdentityChoice { get; set; }

    public DateTime? IdentityChoiceAt { get; set; }

    [MaxLength(40)]
    public string? IdentityChannel { get; set; }

    [MaxLength(40)]
    public string? IdentityFormatId { get; set; }

    /// <summary>The guided retries used for a correctable defect (the policy offers one); 0 on every row written before the column.</summary>
    public int IdentityGuidedRetries { get; set; }

    /// <summary>The idempotency key of the last attempt that did not close the verification (<c>IdentityHeaders.IdempotencyKey</c>, at most 64 characters).</summary>
    [MaxLength(64)]
    public string? IdentityLastAttemptKey { get; set; }

    [MaxLength(80)]
    public string? IdentityLastAttemptResult { get; set; }

    public DateTime? IdentityClosedAt { get; set; }

    [MaxLength(40)]
    public string? IdentityOutcomeCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The link can still be opened by the guest, unless <see cref="ExpiresAt"/> has passed.</summary>
    public bool IsOpen => Status is GuestCheckInSessionStatus.Inviato or GuestCheckInSessionStatus.InCompilazione;

    public bool IsCompleted => Status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato;

    public GuestCheckInSessionStatus EffectiveStatus(DateTime nowUtc) =>
        IsOpen && ExpiresAt < nowUtc ? GuestCheckInSessionStatus.Scaduto : Status;
}

public enum GuestCheckInSessionStatus
{
    Inviato,
    InCompilazione,
    /// <summary>Guest submitted all data. The session stays here until Alloggiati Web returns a real receipt.</summary>
    Completo,
    /// <summary>
    /// The Alloggiati Web communication of the booking has a real receipt (web service client, not implemented). Never set on queueing: CO-11
    /// moved the sessions set that way back to <see cref="Completo"/>.
    /// </summary>
    AlloggiatiInviato,
    Scaduto,
}

public enum GuestCheckInLinkEmailStatus
{
    NotRequested = 0,

    Queued = 1,

    Sent = 2,

    Failed = 3,
}

public static class GuestCheckInLinkEmailErrors
{
    public const string NoRecipient = "no_recipient";

    public const string ProviderNotConfigured = "provider_not_configured";

    public const string QueueFailed = "queue_failed";

    public const string Rejected = "rejected";

    public const string NotDelivered = "not_delivered";

    public const string LinkUnavailable = "link_unavailable";

    public const string LinkNotUsable = "link_not_usable";
}
