using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Alloggiati Web communication (art. 109 TULPS) of one guest of a booking. The pair
/// (<see cref="BookingId"/>, <see cref="GuestId"/>) is unique: it is the idempotency key that keeps the job from
/// being queued twice (guest portal and host check-in) and the communication from being sent twice (CO-11).
/// </summary>
[Table("AlloggiatiWebReports")]
[Index(nameof(BookingId), nameof(GuestId), IsUnique = true)]
public class AlloggiatiWebReport : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public Guid OrgId { get; set; }

    [ForeignKey("Guest")]
    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

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

    public bool ManuallyCompleted { get; set; }

    [MaxLength(100)]
    public string? ScheduledJobId { get; set; }

    public DateTime? ScheduledFor { get; set; }

    /// <summary>
    /// The daily receipt of the Questura account that covers this communication (AW-R05), once retrieved and stored; <c>null</c> until
    /// then. FK to <see cref="AlloggiatiReceipt"/>, SET NULL: a receipt removed at the end of its retention never removes the communication.
    /// </summary>
    public Guid? ReceiptId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// State of an Alloggiati Web communication (CO-11, decision D6). Stored as an integer: the values are explicit
/// and must never be renumbered.
/// </summary>
public enum AlloggiatiWebStatus
{
    DaInviare = 0,

    Inviato = 2,

    Errore = 3,

    /// <summary>Ready, but CasaZen does not transmit: the host must send it on the Questura portal.</summary>
    DaInviareManualmente = 4,

    Rifiutato = 5,

    InviatoManualmente = 6,

    SentReceiptPending = 7,

    Sending = 8,

    PartiallyAccepted = 9,

    ToVerify = 10,

    NotDue = 11,

    EntrustedToChannelManager = 12,

    /// <summary>
    /// At least one guest of the stay could not be written to the channel manager (Octorate refused or did not answer, or a place of the guest is not in
    /// its table): the host retries or corrects the guest, and the errors per guest position say why. Derived, never stored.
    /// </summary>
    ChannelManagerWriteFailed = 13,
}
