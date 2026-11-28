using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// How the steps of a stay go for the bookings of an organization (casazen/backend#978): which steps CasaZen does by itself and which ones the host does
/// by hand, with the parameter a step needs. One row per org (unique <see cref="OrgId"/>); an org with no row is on the defaults of
/// <c>BookingFlowRules</c>, every step automatic. Read by the daily job of the check-in links, the confirmation e-mail, the hourly job of the arrivals and
/// departures (<c>BookingFlowAutomationJob</c>) and the check-out and the cleaning, which declare the property ready.
/// </summary>
/// <remarks>
/// The legal steps are not here: the guest data and the Alloggiati Web communication are never done by CasaZen in the host's place (nothing is sent to
/// the police by CasaZen), and the cleaning at check-out keeps its own setup on each property (<see cref="PropertyCleaningSetup"/>).
/// </remarks>
[Table("BookingFlowSettings")]
[Index(nameof(OrgId), IsUnique = true)]
public class BookingFlowSettings : ITenantOwned
{
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public BookingFlowMode CheckInLinkMode { get; set; } = BookingFlowMode.Automatic;

    public int CheckInLinkDaysBefore { get; set; }

    /// <summary>The arrival: registered by CasaZen once the arrival day is over (unless the host marked a no-show), or by the host.</summary>
    public BookingFlowMode ArrivalMode { get; set; } = BookingFlowMode.Automatic;

    public BookingFlowMode DepartureMode { get; set; } = BookingFlowMode.Automatic;

    public BookingFlowMode PropertyReadyMode { get; set; } = BookingFlowMode.Automatic;

    public PropertyReadyTrigger PropertyReadyTrigger { get; set; } = PropertyReadyTrigger.CleaningDone;

    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum BookingFlowMode
{
    Automatic = 1,

    Manual = 2,
}

public enum PropertyReadyTrigger
{
    CleaningDone = 1,

    CheckOut = 2,
}
