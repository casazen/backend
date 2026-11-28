using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One attempt to transmit the Alloggiati Web communications of a booking (AW-R04, ADR-015-R6): reserved BEFORE the external call,
/// with the Questura account and the credential version it uses and the hash of the exact lines, then completed with the outcome. The
/// attempts stay as the audit of every transmission: one is never deleted to try again.
/// </summary>
/// <remarks>
/// <para><b>At most one open attempt per booking.</b> The filtered unique index <see cref="OpenPerBookingIndexName"/> covers the attempts
/// <see cref="AlloggiatiAttemptStatus.Reserved"/>, <see cref="AlloggiatiAttemptStatus.Sending"/> and
/// <see cref="AlloggiatiAttemptStatus.ToVerify"/>: two concurrent callers (two jobs, the API and a job) can never both hold one, and an
/// outcome nobody knows blocks a new send until somebody verifies it.</para>
/// <para><b>No personal data.</b> <see cref="GuestsJson"/> holds, for each guest, the stay guest id, the position and the outcome, never a
/// name or a document; <see cref="ErrorCode"/> is a stable code.</para>
/// </remarks>
[Table("AlloggiatiSendAttempts")]
public class AlloggiatiSendAttempt : ITenantOwned
{
    public const string OpenPerBookingIndexName = "UIX_AlloggiatiSendAttempts_OpenPerBooking";

    public const int TriggerMaxLength = 40;

    public const int SnapshotHashLength = 64;

    public const int ErrorCodeMaxLength = 100;

    public const string EmptyGuestsJson = "[]";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>The Questura account used (<see cref="PropertyQuesturaCredentials.AccountKey"/>), kept even if the property changes account later.</summary>
    public Guid AccountKey { get; set; }

    public int CredentialVersion { get; set; }

    public AlloggiatiMode Mode { get; set; }

    public AlloggiatiAttemptStatus Status { get; set; } = AlloggiatiAttemptStatus.Reserved;

    [Required]
    [MaxLength(TriggerMaxLength)]
    public string Trigger { get; set; } = string.Empty;

    [Required]
    [MaxLength(SnapshotHashLength)]
    public string SnapshotHash { get; set; } = string.Empty;

    public int GuestCount { get; set; }

    [Required]
    public string GuestsJson { get; set; } = EmptyGuestsJson;

    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public DateTime LeaseExpiresAt { get; set; }

    public DateTime ReservedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public Guid? ReceiptId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Whether an Alloggiati transmission is simulated or real (AW-R04): development, test and demo are simulated and separate, and a
/// simulated outcome never satisfies the real completion. Stored as its number: the values are explicit and never renumbered.
/// </summary>
public enum AlloggiatiMode
{
    Simulated = 1,

    Live = 2,
}

/// <summary>
/// Where an attempt of the Alloggiati transmission stands (AW-R04). Stored as its number: the values are explicit and never
/// renumbered (the filtered unique index of the open attempts reads 1, 3 and 7).
/// </summary>
public enum AlloggiatiAttemptStatus
{
    Reserved = 1,

    TestRejected = 2,

    Sending = 3,

    Accepted = 4,

    PartiallyAccepted = 5,

    Rejected = 6,

    ToVerify = 7,

    /// <summary>Not sent because a rule blocked it (no delegation, no verified account, the emergency switch).</summary>
    Blocked = 8,
}

public static class AlloggiatiAttemptTriggers
{
    public const string ArrivalRegistered = "arrival_registered";

    public const string ArrivalDayJob = "arrival_day_job";

    public const string Host = "host";

    public const string Reconciliation = "reconciliation";
}
