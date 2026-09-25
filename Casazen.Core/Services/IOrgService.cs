using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Org tenant access and plan management. US-004 adds read + MVP plan selection before
/// <c>spec-saas-billing</c> Stripe checkout replaces self-serve tier changes.
/// </summary>
public interface IOrgService
{
    Task<Org?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Org?> GetByUserIdAsync(string userId, CancellationToken cancellationToken = default);

    Task<Org?> GetPublicBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a verified, active custom-domain org for public host resolution (#298).
    /// Requires <see cref="Org.PublicHostMode"/> == <c>CustomDomain</c> and
    /// <see cref="Org.DomainVerificationStatus"/> == <c>Verified</c>; returns <c>null</c> otherwise.
    /// </summary>
    Task<Org?> GetByVerifiedCustomDomainAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an active org by its <see cref="Org.Subdomain"/> label, falling back to
    /// <see cref="Org.Slug"/> when <c>Subdomain</c> is unset (back-compat, #298).
    /// </summary>
    Task<Org?> GetBySubdomainOrSlugAsync(string label, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the user belongs to an org, creating one on first onboarding if needed.
    /// A new org always starts on Starter: paid tiers are granted only by a Stripe subscription (#274).
    /// Idempotent: existing orgs are returned unchanged (plan tier is not overwritten).
    /// </summary>
    Task<Org> EnsureOrgForUserAsync(
        string userId,
        string email,
        string displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the stored plan tier. No entitlement check: callers apply <see cref="PlanChangePolicy"/> first.
    /// </summary>
    Task<Org?> UpdatePlanTierAsync(
        Guid orgId,
        PlanTier planTier,
        CancellationToken cancellationToken = default);

    Task<Org?> GetByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken cancellationToken = default);

    Task<Org?> UpdateBillingProfileAsync(
        Guid orgId,
        string billingCountry,
        string? vatId,
        DateTime? vatValidatedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, Org>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the org's editable identity (A1-22, A1-23): <c>Name</c> (mirrored into <c>DisplayName</c> — there is
    /// no separate branding UI yet, both are shown as the org's name), its public <c>Slug</c> (sanitized, validated,
    /// checked for uniqueness) and its <c>ContactEmail</c>, whose publication on the public booking site is opt-in
    /// (<c>ContactEmailPublic</c>, off by default — GDPR, see <c>.claude/rules/compliance.md</c>). Returns
    /// <c>null</c> when the org does not exist. Throws <c>DomainRuleException</c> for an unusable slug and
    /// <c>DomainConflictException</c> when it is already used by another org.
    /// </summary>
    Task<Org?> UpdateSettingsAsync(
        Guid orgId,
        string name,
        string slug,
        string contactEmail,
        bool contactEmailPublic,
        CancellationToken cancellationToken = default);
}
