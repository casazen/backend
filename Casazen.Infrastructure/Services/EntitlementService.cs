using System.Data;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Enforces per-tier plan limits (AC8). Limits come from a tier→limits map in configuration
/// (<c>Entitlement:Tiers:{tier}:MaxProperties</c>) with provisional defaults; <c>spec-saas-billing</c>
/// owns the final commercial numbers, so reconciliation is config-only (no migration).
/// </summary>
public class EntitlementService(AppDbContext dbContext, IConfiguration configuration) : IEntitlementService
{
    // Provisional defaults (design Open Question #4): Starter = 3, Pro = 50, Scale = unlimited.
    private static readonly IReadOnlyDictionary<PlanTier, int> DefaultMaxProperties = new Dictionary<PlanTier, int>
    {
        [PlanTier.Starter] = 3,
        [PlanTier.Pro] = 50,
        [PlanTier.Scale] = int.MaxValue,
    };

    public async Task<EntitlementResult> GetEntitlementAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var planTier = await dbContext.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => (PlanTier?)o.PlanTier)
            .FirstOrDefaultAsync(cancellationToken) ?? PlanTier.Starter;

        var maxProperties = ResolveMaxProperties(planTier);

        // Count is scoped to the explicit orgId. Under an authenticated request the tenant filter
        // also constrains to the caller's org (which equals orgId), so the count is identical.
        var propertyCount = await dbContext.Properties
            .CountAsync(p => p.OrgId == orgId, cancellationToken);

        return new EntitlementResult(
            orgId,
            planTier.ToString(),
            maxProperties,
            propertyCount,
            propertyCount < maxProperties);
    }

    public async Task<bool> CanAddPropertyAsync(Guid orgId, CancellationToken cancellationToken = default) =>
        (await GetEntitlementAsync(orgId, cancellationToken)).CanAddProperty;

    public async Task<bool> ReservePropertySlotAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            var planTier = await dbContext.Orgs.AsNoTracking()
                .Where(o => o.Id == orgId)
                .Select(o => (PlanTier?)o.PlanTier)
                .FirstOrDefaultAsync(cancellationToken) ?? PlanTier.Starter;

            var maxProperties = ResolveMaxProperties(planTier);
            var propertyCount = await dbContext.Properties
                .CountAsync(p => p.OrgId == orgId, cancellationToken);

            if (propertyCount >= maxProperties)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private int ResolveMaxProperties(PlanTier tier)
    {
        var configured = configuration[$"Entitlement:Tiers:{tier}:MaxProperties"];
        if (int.TryParse(configured, out var value) && value > 0)
            return value;

        return DefaultMaxProperties.TryGetValue(tier, out var fallback)
            ? fallback
            : DefaultMaxProperties[PlanTier.Starter];
    }
}
