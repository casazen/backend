using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed class PropertyOwnershipService(AppDbContext db)
{
    public async Task ValidateSharesAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var totalShares = await db.Set<PropertyOwnership>()
            .Where(po => po.PropertyId == propertyId && po.OwnershipEnd == null)
            .SumAsync(po => po.SharePercent, cancellationToken);

        if (totalShares != 100.00m)
        {
            throw new DomainRuleException("shares_not_hundred", "shares_not_hundred");
        }
    }
}
