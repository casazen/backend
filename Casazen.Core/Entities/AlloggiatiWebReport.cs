using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Alloggiati Web communication (art. 109 TULPS) of one guest of a booking. The pair
/// (<see cref="BookingId"/>, <see cref="GuestId"/>) is unique: it is the idempotency key that keeps the job from
/// being queued twice (guest portal and host check-in) and the communication from being sent twice (CO-11).
/// </summary>
[Table("AlloggiatiWebReports")]
[Index(nameof(BookingId), nameof(GuestId), IsUnique = true)]
public class AlloggiatiWebReport
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    [ForeignKey("Guest")]
    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    /// <summary>
    /// When the communication was sent: the transmission time with a receipt (<see cref="AlloggiatiWebStatus.Inviato"/>)
    /// or the date the host declared (<see cref="AlloggiatiWebStatus.InviatoManualmente"/>). Null while not sent.
    /// </summary>
    public DateTime? ReportedAt { get; set; }

    public AlloggiatiWebStatus Status { get; set; } = AlloggiatiWebStatus.DaInviare;

    /// <summary>
    /// Reference of the real receipt of Alloggiati Web. Required for <see cref="AlloggiatiWebStatus.Inviato"/>
    /// (database check constraint <c>CK_AlloggiatiWebReports_SentRequiresReceipt</c>).
    /// </summary>
    [MaxLength(100)]
    public string? ConfirmationNumber { get; set; }

    /// <summary>Stable error code (snake_case) for <see cref="AlloggiatiWebStatus.Errore"/> and <see cref="AlloggiatiWebStatus.Rifiutato"/>; never free text or personal data.</summary>
    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }

    /// <summary>True when the host declared having sent the communication on the portal.</summary>
    public bool ManuallyCompleted { get; set; }

    /// <summary>Hangfire id of the job scheduled for the arrival day (<see cref="ScheduledFor"/>).</summary>
    [MaxLength(100)]
    public string? ScheduledJobId { get; set; }

    /// <summary>UTC instant the job is scheduled for: the start of the arrival day in Europe/Rome, or later.</summary>
    public DateTime? ScheduledFor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// State of an Alloggiati Web communication (CO-11, decision D6). Stored as an integer: the values are explicit
/// and must never be renumbered.
/// </summary>
public enum AlloggiatiWebStatus
{
    /// <summary>Waiting for the arrival day (Europe/Rome): the portal does not accept a future arrival date.</summary>
    DaInviare = 0,

    // 1 was "Submitted", set without transmitting anything: retired by CO-11, the migration
    // AlloggiatiHonestStatus moved those rows to DaInviareManualmente.

    /// <summary>Transmitted, with the real receipt in <see cref="AlloggiatiWebReport.ConfirmationNumber"/> (CO-13). Never without a receipt.</summary>
    Inviato = 2,

    /// <summary>Technical error while preparing or transmitting the communication.</summary>
    Errore = 3,

    /// <summary>Ready, but CasaZen does not transmit: the host must send it on the Questura portal.</summary>
    DaInviareManualmente = 4,

    /// <summary>Rejected by Alloggiati Web (CO-13).</summary>
    Rifiutato = 5,

    /// <summary>The host declared having sent it on the portal (date in <see cref="AlloggiatiWebReport.ReportedAt"/>). CasaZen holds no receipt.</summary>
    InviatoManualmente = 6,
}
