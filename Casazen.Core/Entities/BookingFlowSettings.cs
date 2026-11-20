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
    /// <summary>The longest id of a user (the size of <c>User.Id</c>).</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (one per org; the row follows its org).</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The online check-in link: e-mailed (or sent in the chat of the portal) by CasaZen, or sent by the host from the booking.</summary>
    public BookingFlowMode CheckInLinkMode { get; set; } = BookingFlowMode.Automatic;

    /// <summary>How many days before the arrival the link goes, when automatic (0 = on the arrival day).</summary>
    public int CheckInLinkDaysBefore { get; set; }

    /// <summary>The arrival: registered by CasaZen once the arrival day is over (unless the host marked a no-show), or by the host.</summary>
    public BookingFlowMode ArrivalMode { get; set; } = BookingFlowMode.Automatic;

    /// <summary>The departure: registered by CasaZen at the check-out time of the property on the departure day, or by the host.</summary>
    public BookingFlowMode DepartureMode { get; set; } = BookingFlowMode.Automatic;

    /// <summary>The property ready for the next guest: declared by CasaZen (<see cref="PropertyReadyTrigger"/>), or by the host.</summary>
    public BookingFlowMode PropertyReadyMode { get; set; } = BookingFlowMode.Automatic;

    /// <summary>When CasaZen declares the property ready, when <see cref="PropertyReadyMode"/> is automatic.</summary>
    public PropertyReadyTrigger PropertyReadyTrigger { get; set; } = PropertyReadyTrigger.CleaningDone;

    /// <summary>The user who saved the settings last (<c>User.Id</c>).</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Who does a step of the stay (stored as an integer; serialized by name).</summary>
public enum BookingFlowMode
{
    /// <summary>CasaZen does it by itself; the host can still do it earlier.</summary>
    Automatic = 1,

    /// <summary>The host does it: CasaZen reminds it as it always did.</summary>
    Manual = 2,
}

/// <summary>When CasaZen declares the property ready for the next guest (stored as an integer; serialized by name).</summary>
public enum PropertyReadyTrigger
{
    /// <summary>
    /// When the cleaning of the stay is done (the supplier completed the request, or the staff member completed the activity). A stay that ends with no cleaning planned is
    /// not declared ready: the host is told, as before, that nobody cleans.
    /// </summary>
    CleaningDone = 1,

    /// <summary>At the check-out, whatever the cleaning.</summary>
    CheckOut = 2,
}
