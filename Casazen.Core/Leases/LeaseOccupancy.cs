using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

public static class LeaseOccupancy
{
    public static Expression<Func<LeaseContract, bool>> RunsOnOrAfter(Guid propertyId, DateTime from) =>
        l => l.PropertyId == propertyId
             && l.Status != LeaseStatus.Draft
             && l.Status != LeaseStatus.Rejected
             && l.CurrentTermEndDate >= from;
}
