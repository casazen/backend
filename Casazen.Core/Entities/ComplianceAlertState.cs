using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// How far the alerts of one kind went for one property and reference (E03/F02/US01/T01, NF-52, CHK-12): the staged-alert counterpart of
/// <see cref="StayAlertState"/> for the alerts that are not tied to one stay (an ISTAT or tourist tax period, the connection of the
/// property to the channel manager). The hourly <c>stay-alerts</c> job sends a message only when it moves <see cref="Stage"/> forward, with
/// an atomic compare-and-set on this row, so a stage is sent once whatever the number of runs, retries or concurrent runs. The triple
/// (<see cref="PropertyId"/>, <see cref="Type"/>, <see cref="ReferenceKey"/>) is unique.
/// </summary>
[Table("ComplianceAlertStates")]
[Index(nameof(PropertyId), nameof(Type), nameof(ReferenceKey), IsUnique = true)]
public class ComplianceAlertState : ITenantOwned
{
    /// <summary>Longest <see cref="ReferenceKey"/>.</summary>
    public const int ReferenceKeyMaxLength = 32;

    /// <summary>The <see cref="ReferenceKey"/> of the alerts about the connection of a property (<see cref="ComplianceAlertType.ConnectionInactive"/>).</summary>
    public const string ConnectionReferenceKey = "connection";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property (TN-2).</summary>
    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public ComplianceAlertType Type { get; set; }

    /// <summary>
    /// What the stages refer to: the period (<c>yyyy-MM</c>) of an ISTAT or tourist tax alert, <see cref="ConnectionReferenceKey"/> for the
    /// connection. When the reference changes (the next period) the sequence starts again for it, because the row is another one.
    /// </summary>
    [Required, MaxLength(ReferenceKeyMaxLength)]
    public string ReferenceKey { get; set; } = string.Empty;

    /// <summary>Last stage sent for <see cref="ReferenceKey"/> (0 = none yet). Meaning per type: see <see cref="ComplianceAlertType"/>.</summary>
    public int Stage { get; set; }

    /// <summary>Messages sent for this property, type and reference.</summary>
    public int AlertCount { get; set; }

    /// <summary>UTC instant of the last message.</summary>
    public DateTime? LastAlertAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Kinds of alert on a property that have their own sequence of stages (stored as an integer, never renumbered).</summary>
public enum ComplianceAlertType
{
    /// <summary>The ISTAT communication of a period is not archived: deadline approaching, overdue, then the limited reminders.</summary>
    IstatPeriod = 1,

    /// <summary>The tourist tax of a period is not declared and paid: deadline approaching, overdue, then the limited reminders.</summary>
    TouristTaxPeriod = 2,

    /// <summary>The police send of the channel manager is not active while a stay is coming: detection, then one more reminder.</summary>
    ConnectionInactive = 3,
}
