using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// Tenant key. Every tenant-scoped table carries an <c>OrgId</c> FK to this entity (RF1).
/// Slug is unique. Stripe identifiers are non-secret account references, never credentials.
/// </summary>
[Table("Orgs")]
public class Org
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Unique, URL-safe org identifier (internal in US-004; public branding added later).</summary>
    [Required, MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    [Required]
    public OrgType OrgType { get; set; } = OrgType.Host;

    [Required]
    public PlanTier PlanTier { get; set; } = PlanTier.Starter;

    [Required, MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? LogoUrl { get; set; }

    [MaxLength(20)]
    public string? ThemeColor { get; set; }

    [MaxLength(50)]
    public string? PublicThemeId { get; set; }

    [MaxLength(2048)]
    public string? HeroImageUrl { get; set; }

    [MaxLength(500)]
    public string? Tagline { get; set; }

    [MaxLength(255)]
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>
    /// Opt-in (A1-22, A1-23): <see cref="ContactEmail"/> is included on the public booking site
    /// (<c>PublicOrgDto</c>) only when this is true. Off by default — the org must actively choose to
    /// publish it (GDPR, see <c>.claude/rules/compliance.md</c>); never inferred from having an email set.
    /// </summary>
    public bool ContactEmailPublic { get; set; }

    /// <summary>Non-secret Stripe customer reference (billing). Set by <c>spec-saas-billing</c>.</summary>
    [MaxLength(255)]
    public string? StripeCustomerId { get; set; }

    /// <summary>Stripe subscription id (<c>sub_xxx</c>) synced from platform webhooks.</summary>
    [MaxLength(255)]
    public string? SubscriptionId { get; set; }

    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.None;

    public DateTime? CurrentPeriodEnd { get; set; }

    [MaxLength(2)]
    public string? BillingCountry { get; set; }

    [MaxLength(32)]
    public string? VatId { get; set; }

    public DateTime? VatIdValidatedAt { get; set; }

    public DateTime? PastDueSince { get; set; }

    /// <summary>Non-secret Stripe Connect account reference (payouts). Used by <c>spec-direct-checkout</c>.</summary>
    [MaxLength(255)]
    public string? StripeConnectedAccountId { get; set; }

    /// <summary>Cached from Stripe <c>account.updated</c> â€” true when the connected account can accept charges.</summary>
    public bool ConnectChargesEnabled { get; set; }

    /// <summary>Cached from Stripe â€” payouts capability on the connected account.</summary>
    public bool ConnectPayoutsEnabled { get; set; }

    /// <summary>Cached from Stripe â€” KYC/details submitted on the connected account.</summary>
    public bool ConnectDetailsSubmitted { get; set; }

    /// <summary>JSON array of outstanding Stripe requirement field names (e.g. <c>["individual.verification.document"]</c>).</summary>
    public string? ConnectRequirementsDueJson { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>STR fiscal profile (issue #3). Distinct from billing <see cref="VatId"/>.</summary>
    public bool HasPartitaIva { get; set; }

    [MaxLength(11)]
    public string? PartitaIvaNumber { get; set; }

    [MaxLength(16)]
    public string? FiscalCode { get; set; }

    public DateTime? FiscalDataRetentionUntil { get; set; }

    // ─── Custom domain / subdomain booking (US-024 / #298) ───────────────────────

    /// <summary>How this org publishes its public booking site: path, subdomain, or custom domain.</summary>
    [Required]
    public PublicHostMode PublicHostMode { get; set; } = PublicHostMode.CasazenPath;

    /// <summary>Normalized FQDN (lowercase, no scheme/port) — Pro/Scale feature; requires verification.</summary>
    [MaxLength(253)]
    public string? CustomDomain { get; set; }

    /// <summary>TXT ownership challenge state for <see cref="CustomDomain"/>.</summary>
    [Required]
    public DomainVerificationStatus DomainVerificationStatus { get; set; } = DomainVerificationStatus.Pending;

    /// <summary>Cryptographically random token expected in the <c>_casazen-challenge</c> TXT record.</summary>
    [MaxLength(128)]
    public string? DomainVerificationToken { get; set; }

    /// <summary>
    /// Why <see cref="DomainVerificationStatus"/> is what it is (BK-17): a stable code of <c>DomainIssues</c> (e.g.
    /// <c>dns_not_pointing</c>), <c>null</c> when the domain is verified. The UI explains it in the user's language.
    /// </summary>
    [MaxLength(64)]
    public string? DomainStatusDetail { get; set; }

    /// <summary>UTC instant of the last check of the domain (manual or by the periodic job), <c>null</c> before the first.</summary>
    public DateTime? DomainCheckedAt { get; set; }

    /// <summary>UTC instant the domain last became <c>Verified</c>; <c>null</c> while it is not.</summary>
    public DateTime? DomainVerifiedAt { get; set; }

    /// <summary>UTC instant the owner set <see cref="CustomDomain"/>: the periodic job stops checking a pending domain a while after it.</summary>
    public DateTime? DomainConfiguredAt { get; set; }

    /// <summary>
    /// UTC instant the domain was found on the Vercel project (added by the platform, BK-17); <c>null</c> when it is not
    /// there. A domain that was there is removed from the project when the owner changes or drops it.
    /// </summary>
    public DateTime? DomainVercelAddedAt { get; set; }

    /// <summary>Consecutive failed checks of a verified domain: it is only demoted after a few, so a DNS hiccup does not take a site down.</summary>
    public int DomainCheckFailures { get; set; }

    /// <summary>TXT record Vercel asks for before the domain may be used on the project (domain already on another Vercel account), host.</summary>
    [MaxLength(253)]
    public string? DomainVercelTxtHost { get; set; }

    /// <summary>Value of the TXT record of <see cref="DomainVercelTxtHost"/>.</summary>
    [MaxLength(500)]
    public string? DomainVercelTxtValue { get; set; }

    /// <summary>Label for <c>{Subdomain}.casazen.it</c>; falls back to <see cref="Slug"/> when null.</summary>
    [MaxLength(63)]
    public string? Subdomain { get; set; }
}
