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
    public const int ReferenceKeyMaxLength = 32;

    public const string ConnectionReferenceKey = "connection";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public ComplianceAlertType Type { get; set; }

    /// <summary>
    /// What the stages refer to: the period (<c>yyyy-MM</c>) of an ISTAT or tourist tax alert, <see cref="ConnectionReferenceKey"/> for the
    /// connection, the arrival day (<c>yyyy-MM-dd</c>) of a Ticino notification alert (the property is the other column of the unique index, so it is
    /// not repeated in the key). When the reference changes (the next period) the sequence starts again for it, because the row is another one.
    /// </summary>
    [Required, MaxLength(ReferenceKeyMaxLength)]
    public string ReferenceKey { get; set; } = string.Empty;

    public int Stage { get; set; }

    public int AlertCount { get; set; }

    public DateTime? LastAlertAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ComplianceAlertType
{
    /// <summary>The ISTAT communication of a period is not archived: deadline approaching, overdue, then the limited reminders.</summary>
    IstatPeriod = 1,

    TouristTaxPeriod = 2,

    ConnectionInactive = 3,

    TicinoNotification = 4,

    SwissNotification = 5,
}
