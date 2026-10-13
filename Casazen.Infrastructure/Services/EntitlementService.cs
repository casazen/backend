using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

public class EntitlementService(AppDbContext dbContext, IConfiguration configuration, IActivityLog? activityLog = null) : IEntitlementService
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
        var paidTier = ResolvePaidTier(storedTier, org?.SubscriptionStatus ?? SubscriptionStatus.None, org?.PastDueSince);
        // BL-01: the open access lifts the orgs that exist; an unknown org id keeps the fail-closed Starter fallback.
        var effectiveTier = org is null ? paidTier : ApplyOpenAccess(paidTier);
        var maxProperties = ResolveMaxProperties(effectiveTier);
        var propertyCount = await CountPropertiesAsync(orgId, cancellationToken);

        return new EntitlementResult(
            orgId, effectiveTier.ToString(), maxProperties, propertyCount, propertyCount < maxProperties,
            OpenAccess: effectiveTier != paidTier);
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

        // The tier the subscription pays for, never the effective one: the open access (BL-01) is an override on read
        // and must not reach the stored tier.
        var paidTier = ResolvePaidTier(org.PlanTier, org.SubscriptionStatus, org.PastDueSince);
        if (paidTier != org.PlanTier)
        {
            // A subscription that no longer pays takes the plan back to Starter: nobody asked, so the line of the activity
            // log (AM-02b) has no actor, and it is written in the save that changes the tier. The tier compared and logged is
            // the paid one: the open access (BL-01) is an override on read and never reaches the stored tier.
            activityLog?.Record(OrgActivity.Of(
                org.Id,
                OrgActivityType.PlanChanged,
                actorUserId: null,
                org.Id.ToString(),
                (OrgActivityDetailKeys.FromTier, org.PlanTier.ToString()),
                (OrgActivityDetailKeys.ToTier, paidTier.ToString()),
                (OrgActivityDetailKeys.Source, PlanChangeSource.Subscription.Code())));

            org.PlanTier = paidTier;
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

    public PlanTier ResolvePaidTier(Org org) =>
        ResolvePaidTier(org.PlanTier, org.SubscriptionStatus, org.PastDueSince);

    /// <summary>
    /// The tier every gate uses: the one the subscription pays for (<see cref="ResolvePaidTier(PlanTier, SubscriptionStatus, DateTime?)"/>),
    /// raised to the tier of the open access when that is on and higher (BL-01, <see cref="OpenAccess"/>).
    /// </summary>
    internal PlanTier ResolveEffectiveTier(PlanTier storedTier, SubscriptionStatus status, DateTime? pastDueSince) =>
        ApplyOpenAccess(ResolvePaidTier(storedTier, status, pastDueSince));

    /// <summary>
    /// A paid tier needs a subscription paying for it (#274, A1-11). Only active, trialing and past due within the
    /// grace period keep the stored tier. Everything else fails closed to Starter: no subscription
    /// (<see cref="SubscriptionStatus.None"/>, also Stripe <c>paused</c>), a first payment not yet succeeded
    /// (<see cref="SubscriptionStatus.Incomplete"/>), retries exhausted (<see cref="SubscriptionStatus.Unpaid"/>),
    /// canceled or <c>incomplete_expired</c>, past due beyond grace or without a start date, and unknown values.
    /// </summary>
    internal PlanTier ResolvePaidTier(PlanTier storedTier, SubscriptionStatus status, DateTime? pastDueSince) =>
        status switch
        {
            SubscriptionStatus.Active or SubscriptionStatus.Trialing => storedTier,
            SubscriptionStatus.PastDue when !IsPastDueGraceExpired(pastDueSince) => storedTier,
            _ => PlanTier.Starter,
        };

    /// <summary>
    /// <c>Entitlement:OpenAccess</c> (BL-01): with the switch on, the tier of the org is at least the configured one; off (the
    /// default) or not valid, the paid tier is returned as it is. Read from the configuration at every call, like the other
    /// <c>Entitlement</c> and <c>Billing</c> settings.
    /// </summary>
    private PlanTier ApplyOpenAccess(PlanTier paidTier) => OpenAccess.Read(configuration).Apply(paidTier);

    private bool IsPastDueGraceExpired(DateTime? pastDueSince)
    {
        // The webhook always records when an org became past due; without it the grace cannot be bounded.
        if (pastDueSince is null)
            return true;

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

    /// <inheritdoc />
    /// <remarks>
    /// The default of a tier is <see cref="PlanCatalog.MaxSeatsFor"/> (Starter 2, Pro 10, Scale unlimited); a positive
    /// <c>Entitlement:Tiers:{Tier}:MaxSeats</c> replaces it. A value that is missing, not a number or not positive is ignored
    /// (the default applies): a typo never locks an org out of inviting nor opens the plan to unlimited people.
    /// </remarks>
    public int ResolveMaxSeats(PlanTier tier)
    {
        var configured = configuration[$"Entitlement:Tiers:{tier}:MaxSeats"];
        if (int.TryParse(configured, out var value) && value > 0)
            return value;

        return PlanCatalog.All.Any(e => e.Tier == tier)
            ? PlanCatalog.MaxSeatsFor(tier)
            : PlanCatalog.MaxSeatsFor(PlanTier.Starter);
    }
}
