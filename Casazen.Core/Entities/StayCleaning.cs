using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The cleaning the automation made for a stay (E24/F01/US01/T02, NF-120): one row for each stay (unique index on the org and the booking), written in the same
/// transaction as the service request or the staff activity it points at. It is the idempotency key of <c>CleaningAutomationService.CreateForStayAsync</c>
/// (a stay gets one automatic cleaning, whatever the number of events and routines that ask for it) and what tells the automatic cleaning of a stay from one the
/// host made by hand, so that a change of the stay moves or cancels only the first.
/// </summary>
/// <remarks>
/// The row holds ids, the kind and the start the cleaning was planned for: never a name, a price or a note. It follows its booking, its property and its org
/// (cascade); the request and the activity it points at are set to <c>null</c> if they are ever removed for good.
/// </remarks>
[Table("StayCleanings")]
public class StayCleaning : ITenantOwned
{
    /// <summary>The unique index that keeps one automatic cleaning for each stay.</summary>
    public const string BookingIndexName = "UIX_StayCleanings_OrgId_BookingId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    /// <summary>The stay (cascade).</summary>
    public Guid BookingId { get; set; }

    /// <summary>The property of the stay (cascade).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>Whether the cleaning is a service request or a staff activity.</summary>
    public PropertyCleanerKind CleanerKind { get; set; }

    /// <summary>The service request of a <see cref="PropertyCleanerKind.Supplier"/> cleaning.</summary>
    public Guid? ServiceRequestId { get; set; }

    /// <summary>The staff activity of a <see cref="PropertyCleanerKind.Staff"/> cleaning.</summary>
    public Guid? InternalActivityId { get; set; }

    /// <summary>
    /// The start of the cleaning (UTC) the automation planned it for: the check-out day of the stay at the time of the setup. What a change of the dates of
    /// the stay is compared with; moved together with the request or the activity.
    /// </summary>
    public DateTime ScheduledStartUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
