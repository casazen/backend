using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public sealed record EntitlementResult(
    Guid OrgId,
    string PlanTier,
    int MaxProperties,
    int PropertyCount,
    bool CanAddProperty);

/// <summary>
/// Enforces per-tier plan limits sourced from a tier→limits map in configuration
/// (<c>Entitlement:Tiers:*</c>). <c>spec-saas-billing</c> is the source of truth for
/// final commercial numbers; this service only reads the map.
/// </summary>
public interface IEntitlementService
{
    /// <summary>Returns the resolved entitlement (limits + usage) for the org.</summary>
    Task<EntitlementResult> GetEntitlementAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>true</c> when the org is below its tier's <c>maxProperties</c> limit and may
    /// create another property; <c>false</c> when the limit is reached.
    /// </summary>
    Task<bool> CanAddPropertyAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="createProperty"/> only while the org is below its plan limit, atomically: on
    /// PostgreSQL the count and the insert run in one transaction holding a per-org advisory lock, so parallel
    /// creates cannot exceed the limit (A1-21). Returns the created property, or <c>null</c> without calling
    /// <paramref name="createProperty"/> when the limit is reached. <paramref name="createProperty"/> must write
    /// through the same scoped <c>AppDbContext</c> (the property service of the request does).
    /// </summary>
    Task<Property?> CreatePropertyWithinLimitAsync(
        Guid orgId,
        Func<Task<Property>> createProperty,
        CancellationToken cancellationToken = default);

    /// <summary>Downgrades stored plan tier when subscription is canceled or past due beyond grace.</summary>
    Task SyncFromSubscriptionAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Effective plan tier of an already loaded org, the one every gate and public flag must use (#274):
    /// the stored tier only while a Stripe subscription pays for it (active, trialing, or past due within
    /// the grace period); Starter otherwise. Fail-closed: no subscription, incomplete (first payment not yet
    /// succeeded), unpaid, canceled, past due beyond grace, and Stripe states the platform does not map (paused…)
    /// all resolve to Starter.
    /// </summary>
    PlanTier ResolveEffectiveTier(Org org);

    /// <summary>
    /// <c>true</c> when the org's effective plan tier (Pro or Scale) unlocks custom-domain
    /// booking sites (#298 / US-024). Starter — and Pro/Scale downgraded to Starter by
    /// <c>ResolveEffectiveTier</c> past-due logic — return <c>false</c>.
    /// </summary>
    Task<bool> CanUseCustomDomainAsync(Guid orgId, CancellationToken cancellationToken = default);
}
