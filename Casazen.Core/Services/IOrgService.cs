using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;

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
    /// Writes the stored plan tier. No entitlement check: callers apply <see cref="PlanChangePolicy"/> first. When the tier
    /// really changes the activity log gets the line <c>PlanChanged</c>, in the same save (AM-02b):
    /// <paramref name="actorUserId"/> is who asked, <paramref name="source"/> says whether it was the org's billing
    /// administrator or CasaZen staff.
    /// </summary>
    Task<Org?> UpdatePlanTierAsync(
        Guid orgId,
        PlanTier planTier,
        string? actorUserId = null,
        PlanChangeSource source = PlanChangeSource.Org,
        CancellationToken cancellationToken = default);

    Task<Org?> GetByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the billing country and the declared VAT id (PL-13: the VAT id is verified by Stripe at checkout, not
    /// here). <paramref name="eInvoice"/> updates the e-invoice data when given; null leaves it unchanged.
    /// </summary>
    Task<Org?> UpdateBillingProfileAsync(
        Guid orgId,
        string billingCountry,
        string? vatId,
        BillingEInvoiceDetails? eInvoice = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, Org>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the org's editable identity (A1-22, A1-23): <c>Name</c> (mirrored into <c>DisplayName</c> — there is
    /// no separate branding UI yet, both are shown as the org's name), its public <c>Slug</c> (sanitized, validated,
    /// checked for uniqueness; the previous one is kept as an <c>OrgSlugAlias</c> so shared links keep working) and
    /// its <c>ContactEmail</c>, whose publication on the public booking site is opt-in (<c>ContactEmailPublic</c>,
    /// off by default — GDPR). Returns <c>null</c> when the org does not exist. Throws <c>DomainRuleException</c>
    /// for an unusable slug and <c>DomainConflictException</c> when another org uses it. When the name or the slug really
    /// changes the activity log gets a line for each (<c>OrgNameChanged</c>, <c>OrgSlugChanged</c>: no value, only the fact),
    /// in the same save (AM-02b); <paramref name="actorUserId"/> is who asked.
    /// </summary>
    Task<Org?> UpdateSettingsAsync(
        Guid orgId,
        string name,
        string slug,
        string contactEmail,
        bool contactEmailPublic,
        string? actorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="slug"/> can become the org's public slug (A1-23): its normalized form and, when it
    /// cannot, the reason code (<c>org_slug_invalid</c>, <c>org_slug_reserved</c>, <c>org_slug_taken</c>). Advisory
    /// only: <see cref="UpdateSettingsAsync"/> checks again under lock. <c>null</c> when the org does not exist.
    /// </summary>
    Task<OrgSlugAvailability?> CheckSlugAvailabilityAsync(
        Guid orgId,
        string slug,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of <see cref="IOrgService.CheckSlugAvailabilityAsync"/>.</summary>
public sealed record OrgSlugAvailability(string Slug, bool Available, string? Code);

/// <summary>
/// Data for the Italian e-invoice of the CasaZen subscription (PL-13). Per field: null leaves the stored value unchanged,
/// an empty string clears it.
/// </summary>
public sealed record BillingEInvoiceDetails(string? SdiRecipientCode, string? PecEmail, string? FiscalCode);
