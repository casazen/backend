using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The length-of-stay discounts of a property (NF-35, ADR-028). The host sends the whole list; it is saved under the property's discount lock,
/// by the minimum of nights (a tier that stays is updated in place, so the unique index is never met by a delete and an insert of the same
/// nights). The host's reads and writes go through the tenant query filter: another org's property does not exist here. The price of a stay
/// reads the tiers of any property (<see cref="GetForPricingAsync"/>): they are public data, like the price of a night.
/// </summary>
public class StayDiscountService(AppDbContext db, ILogger<StayDiscountService> logger) : IStayDiscountService
{
    public async Task<IReadOnlyList<StayDiscountTier>?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (!await db.Properties.AsNoTracking().AnyAsync(p => p.Id == propertyId, cancellationToken))
            return null;

        return await db.PropertyStayDiscounts.AsNoTracking()
            .Where(d => d.PropertyId == propertyId)
            .OrderBy(d => d.MinNights)
            .Select(d => new StayDiscountTier(d.MinNights, d.PercentOff))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StayDiscountTier>?> ReplaceAsync(
        Guid propertyId,
        IReadOnlyList<StayDiscountTier> tiers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        // The rules first: nothing is locked or read for a list that cannot be stored.
        var normalized = StayDiscountRules.Normalize(tiers);

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyStayDiscounts, propertyId.ToString()));

        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);
        if (property is null)
            return null;

        // A discount rewards a stay, which only a short-stay property has.
        PropertyRentalModeRules.EnsureShortRent(property);

        var stored = await db.PropertyStayDiscounts.Where(d => d.PropertyId == propertyId).ToListAsync(cancellationToken);
        foreach (var tier in normalized)
        {
            var row = stored.FirstOrDefault(d => d.MinNights == tier.MinNights);
            if (row is null)
            {
                db.PropertyStayDiscounts.Add(new PropertyStayDiscount
                {
                    PropertyId = propertyId,
                    OrgId = property.OrgId,
                    MinNights = tier.MinNights,
                    PercentOff = tier.PercentOff,
                });
            }
            else
            {
                row.PercentOff = tier.PercentOff;
            }
        }

        db.PropertyStayDiscounts.RemoveRange(stored.Where(d => normalized.All(tier => tier.MinNights != d.MinNights)));
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Stay discounts of property {PropertyId} of org {OrgId} saved: {Tiers} tiers", property.Id, property.OrgId, normalized.Count);
        return normalized;
    }

    public async Task<IReadOnlyList<StayDiscountTier>> GetForPricingAsync(Guid propertyId, CancellationToken cancellationToken = default) =>
        await db.PropertyStayDiscounts.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(d => d.PropertyId == propertyId)
            .OrderBy(d => d.MinNights)
            .Select(d => new StayDiscountTier(d.MinNights, d.PercentOff))
            .ToListAsync(cancellationToken);
}
