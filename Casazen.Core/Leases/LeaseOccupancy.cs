using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// The leases that still tie a property up (PC-05, PM-02): one definition for the rule that refuses to delete a property
/// while a lease runs (<c>PropertyRepository.SoftDeleteAsync</c>) and for the one that refuses to bring a long-term property
/// back to short stays before its leases end (<c>PropertyModeService</c>, decision D16). The rule is the same, the day it is
/// asked for is not: the deletion asks "from today", the change of mode "from the day chosen".
/// </summary>
public static class LeaseOccupancy
{
    /// <summary>
    /// A lease of <paramref name="propertyId"/> that is not a draft nor rejected (a draft is not a contract yet, a rejected
    /// contract will not be registered) and whose last day (<see cref="LeaseContract.EndDate"/>, included) is on or after
    /// <paramref name="from"/>: it keeps the property up to that day. <paramref name="from"/> is an instant, normally the start
    /// of a calendar day of Rome (<c>RomeCalendar.StartOfDayUtc</c>), so a lease that ends on that day still counts.
    /// </summary>
    public static Expression<Func<LeaseContract, bool>> RunsOnOrAfter(Guid propertyId, DateTime from) =>
        l => l.PropertyId == propertyId
             && l.Status != LeaseStatus.Draft
             && l.Status != LeaseStatus.Rejected
             && l.EndDate >= from;
}
