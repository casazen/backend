namespace Casazen.Core.Services;

/// <summary>
/// Resolved plan entitlement for an org: tier, per-tier limits, current usage,
/// and whether another property may be created (AC8).
/// </summary>
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
}
