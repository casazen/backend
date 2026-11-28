using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A change of the <see cref="RentalMode"/> of a property, programmed for a day (PM-02, decisions D16 and D19; report
/// <c>gap/06</c> §4.4): the property goes from <see cref="FromMode"/> to <see cref="ToMode"/> at midnight of Rome of
/// <see cref="EffectiveDate"/>, applied by the hourly <c>property-mode-change</c> job. The row is also the history of the
/// property's modes: it stays after it is applied, cancelled or failed. Runbook:
/// <c>docs/runbooks/property-rental-mode.md</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One open change per property</b>: a partial unique index on <see cref="PropertyId"/> where
/// <see cref="Status"/> is <see cref="PropertyModeChangeStatus.Scheduled"/>. The creation also runs under the property
/// dates lock (<c>BookingRepository.LockPropertyDatesAsync</c>), the one every booking takes, so a change and a booking never
/// check the same property at the same time.</item>
/// <item>The change checks the stays, the imported blocks and the leases when it is created <b>and again when it is
/// applied</b>: what arrived in between fails it (<see cref="PropertyModeChangeStatus.Failed"/>), it never overlaps them.</item>
/// <item>No personal data: ids and dates. <see cref="CreatedByUserId"/> is the Auth0 subject of the host (a column of
/// the same kind as <c>Property.OwnerId</c>); it is never sent to a client.</item>
/// </list>
/// </remarks>
[Table("PropertyModeChanges")]
public class PropertyModeChange : ITenantOwned
{
    public const int FailureReasonMaxLength = 100;

    public const string OneScheduledIndexName = "UIX_PropertyModeChanges_PropertyId_Scheduled";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public RentalMode FromMode { get; set; }

    public RentalMode ToMode { get; set; }

    public DateTime EffectiveDate { get; set; }

    public PropertyModeChangeStatus Status { get; set; } = PropertyModeChangeStatus.Scheduled;

    [MaxLength(255)]
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant the property changed mode; null unless <see cref="Status"/> is <see cref="PropertyModeChangeStatus.Applied"/>.</summary>
    public DateTime? AppliedAt { get; set; }

    /// <summary>UTC instant a person withdrew the change; null unless <see cref="Status"/> is <see cref="PropertyModeChangeStatus.Cancelled"/>.</summary>
    public DateTime? CancelledAt { get; set; }

    [MaxLength(255)]
    public string? CancelledByUserId { get; set; }

    /// <summary>UTC instant the job gave up; null unless <see cref="Status"/> is <see cref="PropertyModeChangeStatus.Failed"/>.</summary>
    public DateTime? FailedAt { get; set; }

    /// <summary>
    /// Why the change failed: the stable <c>code</c> of the rule that stopped it (<c>PropertyModeErrorCodes</c>, for instance
    /// <c>property_mode_blocked_by_bookings</c>), never a message and never data of a guest or a tenant. Null unless
    /// <see cref="Status"/> is <see cref="PropertyModeChangeStatus.Failed"/>.
    /// </summary>
    [MaxLength(FailureReasonMaxLength)]
    public string? FailureReason { get; set; }
}
