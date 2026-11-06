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
    /// <summary>Name of the filtered unique index on <see cref="BookingId"/> of the open attempts.</summary>
    public const string OpenPerBookingIndexName = "UIX_AlloggiatiSendAttempts_OpenPerBooking";

    /// <summary>Longest <see cref="Trigger"/>.</summary>
    public const int TriggerMaxLength = 40;

    /// <summary>Length of a SHA-256 in hexadecimal (<see cref="SnapshotHash"/>).</summary>
    public const int SnapshotHashLength = 64;

    /// <summary>Longest <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeMaxLength = 100;

    /// <summary>The value of <see cref="GuestsJson"/> before an outcome is known for any guest.</summary>
    public const string EmptyGuestsJson = "[]";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the booking when it is reserved (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The booking whose guests are transmitted (FK, RESTRICT: a booking with attempts is never deleted).</summary>
    public Guid BookingId { get; set; }

    /// <summary>The property of the booking when the attempt was reserved.</summary>
    public Guid PropertyId { get; set; }

    /// <summary>The Questura account used (<see cref="PropertyQuesturaCredentials.AccountKey"/>), kept even if the property changes account later.</summary>
    public Guid AccountKey { get; set; }

    /// <summary>The version of the credentials used (<see cref="PropertyQuesturaCredentials.CredentialVersion"/>).</summary>
    public int CredentialVersion { get; set; }

    /// <summary>Simulated (development, test, demo) or live: a simulated attempt never counts as a real transmission.</summary>
    public AlloggiatiMode Mode { get; set; }

    public AlloggiatiAttemptStatus Status { get; set; } = AlloggiatiAttemptStatus.Reserved;

    /// <summary>What started the attempt: one of <see cref="AlloggiatiAttemptTriggers"/>.</summary>
    [Required]
    [MaxLength(TriggerMaxLength)]
    public string Trigger { get; set; } = string.Empty;

    /// <summary>SHA-256 (hexadecimal) of the exact lines sent: the Test and the Send of one attempt carry the same content.</summary>
    [Required]
    [MaxLength(SnapshotHashLength)]
    public string SnapshotHash { get; set; } = string.Empty;

    /// <summary>How many guests (lines) the attempt transmits.</summary>
    public int GuestCount { get; set; }

    /// <summary>
    /// The outcome of each guest as JSON (jsonb): <c>[{ "stayGuestId": "…", "position": 0, "outcome": "accepted" }]</c>. Ids and codes
    /// only, never a name or a document.
    /// </summary>
    [Required]
    public string GuestsJson { get; set; } = EmptyGuestsJson;

    /// <summary>Stable code (snake_case) of a rejection or a failure; never a message with data.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    /// <summary>Until when the caller that reserved the attempt holds it (UTC): an open attempt past it is recovered as to verify, never sent again.</summary>
    public DateTime LeaseExpiresAt { get; set; }

    /// <summary>When the attempt was reserved (UTC), before the external call.</summary>
    public DateTime ReservedAt { get; set; }

    /// <summary>When the outcome was recorded (UTC); <c>null</c> while the attempt is open.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>The daily receipt of the account that covers the attempt, once retrieved (FK, SET NULL).</summary>
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
    /// <summary>Reserved before the external call: open.</summary>
    Reserved = 1,

    /// <summary>The Test of the lines was rejected: nothing was sent.</summary>
    TestRejected = 2,

    /// <summary>The Send is in progress: open.</summary>
    Sending = 3,

    /// <summary>Every guest was acquired.</summary>
    Accepted = 4,

    /// <summary>Some guests were acquired and others rejected: the stay is not complete, the acquired ones are never sent again.</summary>
    PartiallyAccepted = 5,

    /// <summary>The service rejected the transmission.</summary>
    Rejected = 6,

    /// <summary>
    /// The outcome is unknown (timeout, connection lost, crash after a possible acquisition): open until somebody verifies it on the
    /// portal; never sent again by itself.
    /// </summary>
    ToVerify = 7,

    /// <summary>Not sent because a rule blocked it (no delegation, no verified account, the emergency switch).</summary>
    Blocked = 8,
}

/// <summary>What starts an attempt of the Alloggiati transmission (<see cref="AlloggiatiSendAttempt.Trigger"/>).</summary>
public static class AlloggiatiAttemptTriggers
{
    /// <summary>The arrival of the guests was registered.</summary>
    public const string ArrivalRegistered = "arrival_registered";

    /// <summary>The job of the arrival day.</summary>
    public const string ArrivalDayJob = "arrival_day_job";

    /// <summary>The host asked for it.</summary>
    public const string Host = "host";

    /// <summary>A reconciliation after an outcome to verify.</summary>
    public const string Reconciliation = "reconciliation";
}
