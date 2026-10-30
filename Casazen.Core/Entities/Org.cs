using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;

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

    /// <summary>
    /// Name given to an org provisioned without a display name (the access token carries none, A1-02). An org still
    /// named so has not chosen its name yet: the activation checklist (PL-15) keeps asking for it.
    /// </summary>
    public const string PlaceholderName = "La mia organizzazione";

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Unique, URL-safe org identifier (internal in US-004; public branding added later).</summary>
    [Required, MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    [Required]
    public OrgType OrgType { get; set; } = OrgType.Host;

    [Required]
    public PlanTier PlanTier { get; set; } = PlanTier.Starter;

    /// <summary>
    /// Tier downgrade flag: when tier is Agency and this is true, B2B mutations return 403 tier_insufficient
    /// but reads succeed (ADR-010-R5). Agency data is retained in read-only mode.
    /// </summary>
    public bool IsAgencyTierDowngraded { get; set; }

    [Required, MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? LogoUrl { get; set; }

    [MaxLength(20)]
    public string? ThemeColor { get; set; }

    [MaxLength(50)]
    public string? PublicThemeId { get; set; }

    /// <summary>
    /// The font pair of the public site (DB-01): one of <c>PublicSiteFonts.All</c> (editoriale, moderno, classico). Null = the
    /// font its theme starts with. Set by the branding endpoint, read anonymously through <c>PublicOrgDto</c>.
    /// </summary>
    [MaxLength(20)]
    public string? PublicFontId { get; set; }

    [MaxLength(2048)]
    public string? HeroImageUrl { get; set; }

    [MaxLength(500)]
    public string? Tagline { get; set; }

    /// <summary>
    /// The sentence under the slogan in the cover of the public site (DB-03, the "sottotitolo"): plain text, at most
    /// <c>OrgBrandingRules.SubtitleMaxLength</c> characters once the whitespace is collapsed. Null = none: the cover shows the
    /// slogan alone. Set by the branding endpoint, read anonymously through <c>PublicOrgDto</c>.
    /// </summary>
    [MaxLength(500)]
    public string? Subtitle { get; set; }

    /// <summary>
    /// How the person who hosts is named on the public site (DB-03): the host row of the property page, "Scrivi a Giulia".
    /// Free text chosen by the host, at most <c>OrgBrandingRules.HostNameMaxLength</c> characters; null = the site shows
    /// <see cref="DisplayName"/> only. Never derived from the account of the owner.
    /// </summary>
    [MaxLength(100)]
    public string? HostName { get; set; }

    /// <summary>
    /// Phone number the host chose to publish on the booking site (DB-03): <c>+</c> or digits only, 6 to 15 digits
    /// (<c>OrgBrandingRules.NormalizePublicPhone</c>). Unlike <see cref="ContactEmail"/> it is not a datum the org already
    /// had: it is entered on purpose in the public-site branding and exists for no other use, so a value here <b>is</b> the
    /// host's choice to publish it (GDPR, <c>.claude/rules/compliance.md</c>) and an org that never entered one publishes
    /// nothing. Null = no phone on the site. Read anonymously through <c>PublicOrgDto</c> and the property detail.
    /// </summary>
    [MaxLength(20)]
    public string? PublicPhone { get; set; }

    /// <summary>
    /// Legal name of the operator of the booking site (DB-03b-a): the company name, or the name of the person who carries on the
    /// activity, as the site shows it in its footer (the transparency duty of the provider of an information-society service,
    /// of norm art. 7 D.Lgs. 70/2003 — to be confirmed by the lawyer before the production release). Plain text, at most
    /// <c>OrgOperatorRules.LegalNameMaxLength</c> characters once the whitespace is collapsed. Null = the host has not entered
    /// it: the site shows nothing, never an empty line. Written by <c>PUT api/orgs/me/settings</c>, read anonymously through
    /// <c>PublicOrgDto.Operator</c>. Distinct from <see cref="Name"/>, the name of the account.
    /// </summary>
    [MaxLength(200)]
    public string? LegalName { get; set; }

    /// <summary>
    /// Registered office ("sede legale") of the operator of the booking site (DB-03b-a), one free-text line such as
    /// <c>Via Roma 1, 70121 Bari (BA)</c>: plain text, at most <c>OrgOperatorRules.RegisteredOfficeMaxLength</c> characters once
    /// the whitespace is collapsed. Null = not entered. Written and read like <see cref="LegalName"/>.
    /// </summary>
    [MaxLength(300)]
    public string? RegisteredOffice { get; set; }

    /// <summary>
    /// Phone number of the account (AM-08b-b): how CasaZen or the team can reach the org by phone. <c>+</c> or digits only, 6 to 15
    /// digits (<c>OrgSettingsRules.NormalizeContactPhone</c>, the rule of <see cref="PublicPhone"/>). Unlike <see cref="PublicPhone"/> it
    /// is <b>never published</b>: no public DTO carries it, only the org's own administrators read it back
    /// (<c>GET/PUT api/orgs/me/settings</c>). Null = not entered.
    /// </summary>
    [MaxLength(20)]
    public string? ContactPhone { get; set; }

    /// <summary>
    /// Language of the account (AM-08b-b): <c>it</c> (default) or <c>en</c>. It is the language of the emails the org sends to its team
    /// when nobody chooses another (the default of <c>OrgInvitation.Language</c>). The language of the guests is theirs to choose, not
    /// the org's. Existing orgs have <c>it</c>.
    /// </summary>
    [MaxLength(2)]
    public string DefaultLanguage { get; set; } = OrgInvitationRules.DefaultLanguage;

    /// <summary>
    /// Whether a request this org sends to a supplier is also sent to up to three backup suppliers, declared as such (SP-BKP): the
    /// primary has one hour, and the first backup that accepts has the job if the primary does not. <b>On by default</b>, because the
    /// rule is general; the org turns it off with <c>PUT api/orgs/me/service-request-settings</c> and a single request can turn it off
    /// too (<c>useBackupSuppliers</c>). It only matters while the feature flag <c>SupplierRequestBackups</c> is on.
    /// </summary>
    public bool SupplierBackupsEnabled { get; set; } = true;

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

    /// <summary>
    /// Legacy: set by the old VIES stub. Since PL-13 the VAT id that counts for the tax is the one on the Stripe customer,
    /// verified by Stripe (VIES); this column is cleared when the VAT id changes and never set again.
    /// </summary>
    public DateTime? VatIdValidatedAt { get; set; }

    /// <summary>
    /// Billing data for the Italian e-invoice of the CasaZen subscription (PL-13): SDI recipient code ("codice
    /// destinatario"). Name, address and VAT id come from the Stripe customer (Checkout).
    /// </summary>
    [MaxLength(7)]
    public string? BillingSdiRecipientCode { get; set; }

    /// <summary>Certified e-mail (PEC) for the e-invoice, alternative to <see cref="BillingSdiRecipientCode"/>.</summary>
    [MaxLength(255)]
    public string? BillingPecEmail { get; set; }

    /// <summary>Codice fiscale of the billed person or company. Distinct from the STR <see cref="FiscalCode"/>.</summary>
    [MaxLength(16)]
    public string? BillingFiscalCode { get; set; }

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

    /// <summary>
    /// The org can take a direct booking payment (PL-15, A3-26): a connected account is linked <b>and</b> Stripe reports
    /// charges enabled. Having started the Connect onboarding is not enough. The checkout (<c>BookingService</c>), the
    /// activation checklist (<c>OnboardingService</c>) and the <c>acceptsBookings</c> of the public DTOs (DB-03) all read this
    /// one rule, <see cref="AcceptsDirectPayments"/>.
    /// </summary>
    [NotMapped]
    public bool CanTakeDirectPayments => AcceptsDirectPayments(StripeConnectedAccountId, ConnectChargesEnabled);

    /// <summary>
    /// The rule of <see cref="CanTakeDirectPayments"/> on the two columns it reads, for the places that read them in a
    /// projection without loading the org. A booking of the public site, whatever its payment option (card now, card at the
    /// deadline, or a "pay at the property" request), needs a ready connected account: without it the checkout answers 409
    /// <c>direct_booking_payments_not_ready</c>, so this is also the <c>acceptsBookings</c> the site shows.
    /// </summary>
    public static bool AcceptsDirectPayments(string? stripeConnectedAccountId, bool connectChargesEnabled) =>
        !string.IsNullOrWhiteSpace(stripeConnectedAccountId) && connectChargesEnabled;

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
