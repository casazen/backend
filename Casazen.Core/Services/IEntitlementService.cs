using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <param name="OrgId">The org the entitlement is about.</param>
/// <param name="PlanTier">The <b>effective</b> tier name: what the limits below are those of.</param>
/// <param name="MaxProperties">Properties the effective tier allows (<see cref="int.MaxValue"/> = unlimited).</param>
/// <param name="PropertyCount">Properties of the org.</param>
/// <param name="CanAddProperty"><c>true</c> while <paramref name="PropertyCount"/> is below <paramref name="MaxProperties"/>.</param>
/// <param name="OpenAccess">
/// <c>true</c> when <paramref name="PlanTier"/> is higher than the tier the subscription pays for, because
/// <c>Entitlement:OpenAccess</c> is on (BL-01, <see cref="Casazen.Core.Services.OpenAccess"/>). <c>false</c> when the switch is
/// off, and also when it is on but changes nothing for this org (it already has that tier or a higher one).
/// </param>
public sealed record EntitlementResult(
    Guid OrgId,
    string PlanTier,
    int MaxProperties,
    int PropertyCount,
    bool CanAddProperty,
    bool OpenAccess = false);

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
    /// all resolve to Starter. With the open access on (<c>Entitlement:OpenAccess</c>, BL-01) that result is raised to the
    /// configured tier, never lowered: limits, custom domain, "Realizzato con" and seats follow the raised tier, while the
    /// stored tier and the Stripe data stay what they are. For the tier the subscription pays for see
    /// <see cref="ResolvePaidTier"/>.
    /// </summary>
    PlanTier ResolveEffectiveTier(Org org);

    /// <summary>
    /// How many people (active members plus pending invitations) an org on <paramref name="tier"/> may have (AM-02,
    /// decisions D13 and D35): <see cref="PlanCatalog.MaxSeatsFor"/> unless <c>Entitlement:Tiers:{Tier}:MaxSeats</c> sets a
    /// positive number; <see cref="int.MaxValue"/> = unlimited. Pass the <b>effective</b> tier
    /// (<see cref="ResolveEffectiveTier"/>): with a subscription not in good standing it is Starter, so the members stay
    /// and the new invitations are blocked.
    /// </summary>
    int ResolveMaxSeats(PlanTier tier);

    /// <summary>
    /// <c>true</c> when the org's effective plan tier (Pro or Scale) unlocks custom-domain
    /// booking sites (#298 / US-024). Starter — and Pro/Scale downgraded to Starter by
    /// <c>ResolveEffectiveTier</c> past-due logic — return <c>false</c>.
    /// </summary>
    Task<bool> CanUseCustomDomainAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tier the org's subscription pays for (BL-01): <see cref="ResolveEffectiveTier"/> <b>without</b> the open access.
    /// It is what the plan change rules compare with (<see cref="PlanChangePolicy"/>: without a subscription a plan above
    /// it is refused, whatever the open access says) and what <see cref="SyncFromSubscriptionAsync"/> stores. Never use it to
    /// gate a feature: that is <see cref="ResolveEffectiveTier"/>.
    /// </summary>
    PlanTier ResolvePaidTier(Org org);
}
