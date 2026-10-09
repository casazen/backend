using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// One band of the supplier's weekly working hours (SP-03, <c>api/supplier/availability/hours</c>): the supplier works on
/// <see cref="Weekday"/> from <see cref="StartMinute"/> to <see cref="EndMinute"/>, on the wall clock of Europe/Rome
/// (a supplier has up to three bands a day, for example morning and afternoon; a weekday with no band is a rest day).
/// </summary>
/// <remarks>
/// <para><b>Minutes, not instants.</b> The band is a wall-clock time of Rome, so it does not move when daylight saving
/// time changes: 09:00 stays 09:00. The planner turns it into UTC for each date with <c>RomeCalendar.ToUtc</c>, which
/// also decides what happens to a time that does not exist (29 March) or happens twice (25 October).</para>
/// <para><b>Tenancy.</b> Keyed by the supplier org (<see cref="OrgId"/> = <see cref="SupplierProfile.OrgId"/>), like
/// <see cref="SupplierAvailability"/> and <see cref="SupplierServiceListing"/>: it is <b>not</b> <c>ITenantOwned</c> (a
/// supplier acts as <c>User.SupplierOrgId</c> and a supplier-only account has no <c>User.OrgId</c>, so the host-org filter
/// would give it no rows). It is in the allow-list of <c>TenantQueryFilterArchitectureTests</c> and every statement
/// carries an explicit <c>OrgId</c> predicate (<c>SupplierAgendaService</c> only, <c>SupplierAgendaTenancyTests</c>).</para>
/// </remarks>
[Table("SupplierWorkingHours")]
public class SupplierWorkingHours
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The supplier org the hours belong to (<see cref="SupplierProfile.OrgId"/>).</summary>
    [Required]
    public Guid OrgId { get; set; }

    /// <summary>Day of the week, stored as the number of <see cref="DayOfWeek"/> (Sunday is 0). The API shows the name.</summary>
    public DayOfWeek Weekday { get; set; }

    /// <summary>Minutes after midnight (Rome) at which the band starts: 0 to 1439.</summary>
    public int StartMinute { get; set; }

    /// <summary>Minutes after midnight (Rome) at which the band ends, after <see cref="StartMinute"/>: up to 1440 (midnight).</summary>
    public int EndMinute { get; set; }

    [ForeignKey(nameof(OrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}
