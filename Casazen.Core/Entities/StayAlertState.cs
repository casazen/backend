using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// How far the alerts of one kind went for one stay (CO-10, A5-11, A6-07): the hourly <c>stay-alerts</c> job sends a
/// message only when it moves <see cref="Stage"/> forward, with an atomic compare-and-set on this row, so a stage is
/// sent once whatever the number of runs, retries or concurrent runs. The pair (<see cref="BookingId"/>,
/// <see cref="Type"/>) is unique.
/// </summary>
[Table("StayAlertStates")]
[Index(nameof(BookingId), nameof(Type), IsUnique = true)]
public class StayAlertState : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Booking))]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public StayAlertType Type { get; set; }

    /// <summary>
    /// Date-only value the stages refer to: the check-in date for the Alloggiati alerts, the check-out date for the
    /// check-out reminder. When the booking's date changes the sequence starts again for the new date.
    /// </summary>
    public DateTime ReferenceDate { get; set; }

    public int Stage { get; set; }

    public int AlertCount { get; set; }

    public DateTime? LastAlertAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum StayAlertType
{
    /// <summary>
    /// Alloggiati Web communication not sent: stages of <see cref="Services.AlloggiatiAlertStage"/> (guest data missing,
    /// deadline approaching, overdue, then the limited daily overdue reminders).
    /// </summary>
    AlloggiatiDeadline = 1,

    AlloggiatiFailed = 2,

    CheckoutReminder = 3,

    ReceiptMissing = 4,

    ShortStayAtRisk = 5,

    OverThirtyNights = 6,
}
