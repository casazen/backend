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

    /// <summary>Tenant of the row, copied from the booking (TN-2).</summary>
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

    /// <summary>Last stage sent for <see cref="ReferenceDate"/> (0 = none yet). Meaning per type: see <see cref="StayAlertType"/>.</summary>
    public int Stage { get; set; }

    /// <summary>Messages sent for this stay and type, all dates included (each message is one email plus one push).</summary>
    public int AlertCount { get; set; }

    /// <summary>UTC instant of the last message.</summary>
    public DateTime? LastAlertAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Kinds of stay alert with their own sequence of stages (stored as an integer).</summary>
public enum StayAlertType
{
    /// <summary>
    /// Alloggiati Web communication not sent: stages of <see cref="Services.AlloggiatiAlertStage"/> (guest data missing,
    /// deadline approaching, overdue, then the limited daily overdue reminders).
    /// </summary>
    AlloggiatiDeadline = 1,

    /// <summary>Communication rejected by the portal or failed (<see cref="AlloggiatiWebStatus.Errore"/>, <see cref="AlloggiatiWebStatus.Rifiutato"/>): one stage.</summary>
    AlloggiatiFailed = 2,

    /// <summary>Check-out day reminder to the host: one stage.</summary>
    CheckoutReminder = 3,
}
