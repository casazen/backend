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
    public const string BookingIndexName = "UIX_StayCleanings_OrgId_BookingId";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public Guid PropertyId { get; set; }

    public PropertyCleanerKind CleanerKind { get; set; }

    public Guid? ServiceRequestId { get; set; }

    public Guid? InternalActivityId { get; set; }

    public DateTime ScheduledStartUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
