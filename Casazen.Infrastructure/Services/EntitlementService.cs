using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

public class EntitlementService(AppDbContext dbContext, IConfiguration configuration) : IEntitlementService
{
    private static readonly IReadOnlyDictionary<PlanTier, int> DefaultMaxProperties = new Dictionary<PlanTier, int>
    {
        [PlanTier.Starter] = 3,
        [PlanTier.Pro] = 50,
        [PlanTier.Scale] = int.MaxValue,
    };

    public async Task<EntitlementResult> GetEntitlementAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.PlanTier, o.SubscriptionStatus, o.PastDueSince })
            .FirstOrDefaultAsync(cancellationToken);

        var storedTier = org?.PlanTier ?? PlanTier.Starter;
        var effectiveTier = ResolveEffectiveTier(storedTier, org?.SubscriptionStatus ?? SubscriptionStatus.None, org?.PastDueSince);
        var maxProperties = ResolveMaxProperties(effectiveTier);
        var propertyCount = await CountPropertiesAsync(orgId, cancellationToken);

        return new EntitlementResult(orgId, effectiveTier.ToString(), maxProperties, propertyCount, propertyCount < maxProperties);
    }

    public async Task<bool> CanAddPropertyAsync(Guid orgId, CancellationToken cancellationToken = default) =>
        (await GetEntitlementAsync(orgId, cancellationToken)).CanAddProperty;

    public async Task<Property?> CreatePropertyWithinLimitAsync(
        Guid orgId,
        Func<Task<Property>> createProperty,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createProperty);

        // A1-21: the count and the insert share one transaction and a per-org advisory lock, so two parallel
        // creates cannot both take the last slot. Disposing without commit rolls back (limit reached or error).
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            dbContext,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.OrgPropertySlot, orgId.ToString("N")));

        if (!(await GetEntitlementAsync(orgId, cancellationToken)).CanAddProperty)
            return null;

        var created = await createProperty();
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return created;
    }

    // IgnoreQueryFilters (tenant only): usage belongs to the org passed in, whoever the caller is. The admin plan
    // change reads another org (it showed usage=0), and the limit check must count every row of the org (A1-21).
    private Task<int> CountPropertiesAsync(Guid orgId, CancellationToken cancellationToken) =>
        dbContext.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .CountAsync(p => p.OrgId == orgId, cancellationToken);

    public async Task SyncFromSubscriptionAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null || org.SubscriptionStatus == SubscriptionStatus.None)
            return;

        var effectiveTier = ResolveEffectiveTier(org.PlanTier, org.SubscriptionStatus, org.PastDueSince);
        if (effectiveTier != org.PlanTier)
        {
            org.PlanTier = effectiveTier;
            org.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> CanUseCustomDomainAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.PlanTier, o.SubscriptionStatus, o.PastDueSince })
            .FirstOrDefaultAsync(cancellationToken);

        if (org is null)
            return false;

        var effectiveTier = ResolveEffectiveTier(org.PlanTier, org.SubscriptionStatus, org.PastDueSince);
        return effectiveTier is PlanTier.Pro or PlanTier.Scale;
    }

    public PlanTier ResolveEffectiveTier(Org org) =>
        ResolveEffectiveTier(org.PlanTier, org.SubscriptionStatus, org.PastDueSince);

    /// <summary>
    /// A paid tier needs a subscription paying for it (#274). <see cref="SubscriptionStatus.None"/> covers orgs
    /// that never subscribed and Stripe states that are not mapped (incomplete, incomplete_expired, paused):
    /// like canceled, past due beyond grace and any unknown value they fail closed to Starter.
    /// </summary>
    internal PlanTier ResolveEffectiveTier(PlanTier storedTier, SubscriptionStatus status, DateTime? pastDueSince) =>
        status switch
        {
            SubscriptionStatus.Active or SubscriptionStatus.Trialing => storedTier,
            SubscriptionStatus.PastDue when !IsPastDueGraceExpired(pastDueSince) => storedTier,
            _ => PlanTier.Starter,
        };

    private bool IsPastDueGraceExpired(DateTime? pastDueSince)
    {
        if (pastDueSince is null)
            return false;

        var graceDays = configuration.GetValue("Billing:PastDueGraceDays", 7);
        return DateTime.UtcNow > pastDueSince.Value.AddDays(graceDays);
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
