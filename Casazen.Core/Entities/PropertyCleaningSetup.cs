using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The default cleaner of a property at the check-out of its stays, with the price and the time agreed (E24/F01/US01/T01, NF-120, decision O11): one row for
/// each property (unique index on <see cref="PropertyId"/>). The automation of the cleaning at check-out (<c>CleaningAutomationService</c>, the routine action
/// <c>CreateCleaningRequest</c>) reads it for the stays of the property: a supplier gets a service request at the agreed price, a member of the staff gets a
/// staff activity with its cost. A property with no row, or with an inactive one, is not automated: nothing is created and the "To do" of the stay that ended
/// without a cleaning appears as it always did.
/// </summary>
/// <remarks>
/// <para><b>The cleaner.</b> <see cref="CleanerKind"/> says which of <see cref="SupplierOrgId"/> and <see cref="StaffUserId"/> is set, and only that one (a
/// database check keeps the two apart). The supplier is an organization with an active supplier profile that covers the comune of the property, preferably
/// one of the org's <see cref="TrustedSupplier"/>s; the staff member is an active member of the org who reaches the property. Both are checked again when the
/// cleaning is created: a supplier suspended since, or a member deactivated, never gets a job.</para>
/// <para><b>The price.</b> <see cref="PriceCents"/> is the price agreed for one cleaning, in cents of the <b>currency of the property</b> (the row keeps no
/// currency of its own), the gross price the cleaner asks (decision D4 of the service requests: no VAT split). It is required, from 1 cent, for a supplier and
/// optional for staff (a member of the staff whose work has no cost to recharge).</para>
/// <para><b>The time.</b> <see cref="StartTime"/> is a wall-clock time on the clock of the property, on the check-out day of the stay; without one the check-out
/// time of the property applies (<c>Property.CheckOutBy</c>, 10:00 when it has none). <see cref="DurationMinutes"/> is how long the cleaning lasts; without
/// one it is two hours (<c>CleaningSchedule.DefaultDurationMinutes</c>), the length a request without a catalog service gets.</para>
/// <para>The row follows its property and its org (cascade), like the supplier it names: it never outlives what it belongs to, and never blocks their erasure.</para>
/// </remarks>
[Table("PropertyCleaningSetups")]
public class PropertyCleaningSetup : ITenantOwned
{
    /// <summary>The unique index that keeps one setup for each property.</summary>
    public const string PropertyIndexName = "UIX_PropertyCleaningSetups_PropertyId";

    /// <summary>The check constraint that ties the kind of cleaner to the columns it uses.</summary>
    public const string CleanerCheckName = "CK_PropertyCleaningSetups_Cleaner";

    /// <summary>The longest id of a staff user (the size of <c>User.Id</c>).</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property (cascade, unique).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>Who cleans: a supplier or a member of the staff.</summary>
    public PropertyCleanerKind CleanerKind { get; set; }

    /// <summary>The supplier org, for <see cref="PropertyCleanerKind.Supplier"/> only (cascade).</summary>
    public Guid? SupplierOrgId { get; set; }

    /// <summary>The member of the org (<c>User.Id</c>), for <see cref="PropertyCleanerKind.Staff"/> only.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? StaffUserId { get; set; }

    /// <summary>
    /// The price agreed for one cleaning, in cents of the currency of the property; from 1 cent (the bound of <c>ServiceRequestLimits.MaxAmountCents</c> above).
    /// Required for a supplier, optional for staff.
    /// </summary>
    public int? PriceCents { get; set; }

    /// <summary>The time the cleaning starts on the check-out day, on the clock of the property; <c>null</c> = the check-out time of the property.</summary>
    public TimeOnly? StartTime { get; set; }

    /// <summary>How long the cleaning lasts, in minutes (5 to 1,440); <c>null</c> = the default length.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>The automation uses the setup only while it is active; an inactive one is kept for the host to switch on again.</summary>
    public bool Active { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who saved the setup last (<c>User.Id</c>).</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
