using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// Stable code of the rule that keeps one property from having two leases on the same days (LR-OVERLAP). A code is never renamed: add
/// a new one. The runbook <c>docs/runbooks/long-rent-lease-draft.md</c> lists it with its status and the members of the problem.
/// </summary>
public static class LeasePeriodCodes
{
    /// <summary>
    /// 409: another lease of the same property covers some of the same days, so this one cannot become a contract (review confirmed,
    /// handed out or sent for signature, signed copy or imported contract recorded). Nothing was written. The problem carries
    /// <c>conflictingLeaseId</c>, <c>conflictingFrom</c> and <c>conflictingTo</c> (<c>yyyy-MM-dd</c>) and no name of a person.
    /// </summary>
    public const string Overlaps = "lease_period_overlaps";
}

/// <summary>The lease that is in the way of another one on the same property: its id and the first and last day it covers (Europe/Rome days).</summary>
public sealed record LeasePeriodConflict(Guid ConflictingLeaseId, DateOnly ConflictingFrom, DateOnly ConflictingTo);

/// <summary>
/// The rule of the days of a lease (LR-OVERLAP, product owner, 2026-10-25: «sullo stesso appartamento non può essere: se c'è già un
/// contratto in essere nelle stesse date non posso crearne 2»): two leases of the same property cannot cover the same days. It supersedes
/// the old review warning of decision LR-D14 (one may let by rooms), which let a second lease through.
/// </summary>
/// <remarks>
/// <para><b>The period of a lease</b> is <c>[start, last day]</c>, both days included, where the last day is the day it was ended
/// (<see cref="LeaseContract.TerminatedOn"/>) and otherwise the end of the period it is in now (<see cref="LeaseContract.CurrentTermEndDate"/>,
/// which a tacit renewal moves forward, LR-14). A lease ended early frees the days after its last day. Two periods that follow each other
/// (one ends on 31 October, the next starts on 1 November) do not overlap.</para>
/// <para><b>Which leases count</b>: every lease of the property but the one asked about, except a rejected one (it will never take effect)
/// and a draft that does not have its two dates yet (it covers no days). A draft that has both dates does count: it is a contract in the
/// making, and the landlord is told about it in the wizard before anything is blocked.</para>
/// </remarks>
public static class LeasePeriodRules
{
    /// <summary>The last day of the lease: the day it was ended, otherwise the end of the period it is in now.</summary>
    public static DateTime EffectiveEnd(DateTime? terminatedOn, DateTime currentTermEndDate) => terminatedOn ?? currentTermEndDate;

    /// <summary>The last day of <paramref name="lease"/> (<see cref="EffectiveEnd(DateTime?, DateTime)"/>).</summary>
    public static DateTime EffectiveEnd(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return EffectiveEnd(lease.TerminatedOn, lease.CurrentTermEndDate);
    }

    /// <summary>
    /// True when the two periods (first and last day included) share at least one day. A period whose end is before its start covers no
    /// day and overlaps nothing.
    /// </summary>
    public static bool Overlaps(DateTime firstStart, DateTime firstEnd, DateTime secondStart, DateTime secondEnd) =>
        firstStart <= firstEnd && secondStart <= secondEnd && firstStart <= secondEnd && secondStart <= firstEnd;

    /// <summary>
    /// True when both dates are real days and the period is not empty: a draft that was not given both its dates (the starting value of the
    /// column is <see cref="LeaseDraftDefaults.PlaceholderDate"/>) has no period to protect or to check.
    /// </summary>
    public static bool HasPeriod(DateTime start, DateTime end) =>
        !LeaseDraftDefaults.IsPlaceholder(start) && !LeaseDraftDefaults.IsPlaceholder(end) && start <= end;

    /// <summary>
    /// The leases of <paramref name="propertyId"/> that stand in the way of a lease with the period <paramref name="from"/> to
    /// <paramref name="to"/> (first and last day included): the rule above, written once for SQL. <paramref name="exceptLeaseId"/> is the
    /// lease asked about, which never conflicts with itself.
    /// </summary>
    public static Expression<Func<LeaseContract, bool>> InTheWayOf(Guid propertyId, Guid exceptLeaseId, DateTime from, DateTime to)
    {
        var placeholder = LeaseDraftDefaults.PlaceholderDate;
        return l => l.PropertyId == propertyId
                    && l.Id != exceptLeaseId
                    && l.Status != LeaseStatus.Rejected
                    && l.StartDate < placeholder
                    && l.CurrentTermEndDate < placeholder
                    && l.StartDate <= (l.TerminatedOn ?? l.CurrentTermEndDate)
                    && l.StartDate <= to
                    && (l.TerminatedOn ?? l.CurrentTermEndDate) >= from;
    }
}
