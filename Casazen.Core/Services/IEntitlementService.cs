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
    /// Atomically reserves a property slot under a Serializable transaction.
    /// Returns <c>false</c> when the plan limit is already reached.
    /// </summary>
    Task<bool> ReservePropertySlotAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Downgrades stored plan tier when subscription is canceled or past due beyond grace.</summary>
    Task SyncFromSubscriptionAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>true</c> when the org's effective plan tier (Pro or Scale) unlocks custom-domain
    /// booking sites (#298 / US-024). Starter — and Pro/Scale downgraded to Starter by
    /// <c>ResolveEffectiveTier</c> past-due logic — return <c>false</c>.
    /// </summary>
    Task<bool> CanUseCustomDomainAsync(Guid orgId, CancellationToken cancellationToken = default);
}
