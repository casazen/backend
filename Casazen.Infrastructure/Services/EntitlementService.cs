using System.Data;
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
        var propertyCount = await dbContext.Properties.CountAsync(p => p.OrgId == orgId, cancellationToken);

        return new EntitlementResult(orgId, effectiveTier.ToString(), maxProperties, propertyCount, propertyCount < maxProperties);
    }

    public async Task<bool> CanAddPropertyAsync(Guid orgId, CancellationToken cancellationToken = default) =>
        (await GetEntitlementAsync(orgId, cancellationToken)).CanAddProperty;

    public async Task<bool> ReservePropertySlotAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var org = await dbContext.Orgs.AsNoTracking()
                .Where(o => o.Id == orgId)
                .Select(o => new { o.PlanTier, o.SubscriptionStatus, o.PastDueSince })
                .FirstOrDefaultAsync(cancellationToken);

            var storedTier = org?.PlanTier ?? PlanTier.Starter;
            var effectiveTier = ResolveEffectiveTier(storedTier, org?.SubscriptionStatus ?? SubscriptionStatus.None, org?.PastDueSince);
            var maxProperties = ResolveMaxProperties(effectiveTier);
            var propertyCount = await dbContext.Properties.CountAsync(p => p.OrgId == orgId, cancellationToken);

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

    internal PlanTier ResolveEffectiveTier(PlanTier storedTier, SubscriptionStatus status, DateTime? pastDueSince) =>
        status switch
        {
            SubscriptionStatus.None => storedTier,
            SubscriptionStatus.Active or SubscriptionStatus.Trialing => storedTier,
            SubscriptionStatus.PastDue when !IsPastDueGraceExpired(pastDueSince) => storedTier,
            SubscriptionStatus.PastDue or SubscriptionStatus.Canceled => PlanTier.Starter,
            _ => storedTier,
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
