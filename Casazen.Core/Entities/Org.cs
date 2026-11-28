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

    public const string PlaceholderName = "La mia organizzazione";

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    [Required]
    public OrgType OrgType { get; set; } = OrgType.Host;

    [Required]
    public PlanTier PlanTier { get; set; } = PlanTier.Starter;

    public bool IsAgencyTierDowngraded { get; set; }

    public string? CommercialSignupJson { get; set; }
    public bool CommercialPaymentConfirmed { get; set; }
    public string? CommercialPaidAddonsJson { get; set; }
    public int CommercialRoomAllowance { get; set; }


    public bool IsAgency { get; set; }

    [ConcurrencyCheck]
    public ComplianceSenderType? DefaultComplianceSender { get; set; }
    [ConcurrencyCheck]
    public DateTime? DefaultComplianceSenderChangedAt { get; set; }
    [MaxLength(255)]
    public string? DefaultComplianceSenderChangedByUserId { get; set; }


    [Required, MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? LogoUrl { get; set; }

    [MaxLength(20)]
    public string? ThemeColor { get; set; }

    [MaxLength(50)]
    public string? PublicThemeId { get; set; }

    [MaxLength(20)]
    public string? PublicFontId { get; set; }

    [MaxLength(2048)]
    public string? HeroImageUrl { get; set; }

    [MaxLength(500)]
    public string? Tagline { get; set; }

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

    [MaxLength(2)]
    public string DefaultLanguage { get; set; } = OrgInvitationRules.DefaultLanguage;

    /// <summary>
    /// Whether a request this org sends to a supplier is also sent to up to three backup suppliers, declared as such (SP-BKP): the
    /// primary has one hour, and the first backup that accepts has the job if the primary does not. <b>On by default</b>, because the
    /// rule is general; the org turns it off with <c>PUT api/orgs/me/service-request-settings</c> and a single request can turn it off
    /// too (<c>useBackupSuppliers</c>). It only matters while the feature flag <c>SupplierRequestBackups</c> is on.
    /// </summary>
    public bool SupplierBackupsEnabled { get; set; } = true;

    public bool TouristTaxOnlinePaymentEnabled { get; set; }

    /// <summary>
    /// Whether the lock code is held back until the tourist tax is paid (E25/F01/US01/T01, <c>RequiredBeforeAccessCode</c>): the server holds it back
    /// (<c>IAccessCodeService.ReleaseToGuestAsync</c>) while the stay owes a tax the guest can pay online and has not paid, and the guest page asks for the tax
    /// first. <b>Off by default</b>: without it the tax never holds the code back. Read only while <see cref="TouristTaxOnlinePaymentEnabled"/> is on.
    /// </summary>
    public bool TouristTaxRequiredBeforeAccessCode { get; set; }

    [MaxLength(255)]
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>
    /// Opt-in (A1-22, A1-23): <see cref="ContactEmail"/> is included on the public booking site
    /// (<c>PublicOrgDto</c>) only when this is true. Off by default — the org must actively choose to
    /// publish it (GDPR, see <c>.claude/rules/compliance.md</c>); never inferred from having an email set.
    /// </summary>
    public bool ContactEmailPublic { get; set; }

    [MaxLength(255)]
    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// Financial Connections account (<c>fca_…</c>) the landlord linked for an optional bank debit. A Stripe account id,
    /// never an IBAN or account number. Null while the org pays by the manual bank transfer.
    /// </summary>
    [MaxLength(255)]
    public string? FinancialConnectionsAccountId { get; set; }

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

    [MaxLength(255)]
    public string? BillingPecEmail { get; set; }

    /// <summary>Codice fiscale of the billed person or company. Distinct from the STR <see cref="FiscalCode"/>.</summary>
    [MaxLength(16)]
    public string? BillingFiscalCode { get; set; }

    public DateTime? PastDueSince { get; set; }

    [MaxLength(255)]
    public string? StripeConnectedAccountId { get; set; }

    public bool ConnectChargesEnabled { get; set; }

    public bool ConnectPayoutsEnabled { get; set; }

    public bool ConnectDetailsSubmitted { get; set; }

    public string? ConnectRequirementsDueJson { get; set; }

    /// <summary>
    /// The org can take a direct booking payment (PL-15, A3-26): a connected account is linked <b>and</b> Stripe reports
    /// charges enabled. Having started the Connect onboarding is not enough. The checkout (<c>BookingService</c>), the
    /// activation checklist (<c>OnboardingService</c>) and the <c>acceptsBookings</c> of the public DTOs (DB-03) all read this
    /// one rule, <see cref="AcceptsDirectPayments"/>.
    /// </summary>
    [NotMapped]
    public bool CanTakeDirectPayments => AcceptsDirectPayments(StripeConnectedAccountId, ConnectChargesEnabled);

    public static bool AcceptsDirectPayments(string? stripeConnectedAccountId, bool connectChargesEnabled) =>
        !string.IsNullOrWhiteSpace(stripeConnectedAccountId) && connectChargesEnabled;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasPartitaIva { get; set; }

    [MaxLength(11)]
    public string? PartitaIvaNumber { get; set; }

    [MaxLength(16)]
    public string? FiscalCode { get; set; }

    public DateTime? FiscalDataRetentionUntil { get; set; }

    [Required]
    public PublicHostMode PublicHostMode { get; set; } = PublicHostMode.CasazenPath;

    [MaxLength(253)]
    public string? CustomDomain { get; set; }

    [Required]
    public DomainVerificationStatus DomainVerificationStatus { get; set; } = DomainVerificationStatus.Pending;

    [MaxLength(128)]
    public string? DomainVerificationToken { get; set; }

    [MaxLength(64)]
    public string? DomainStatusDetail { get; set; }

    public DateTime? DomainCheckedAt { get; set; }

    public DateTime? DomainVerifiedAt { get; set; }

    public DateTime? DomainConfiguredAt { get; set; }

    public DateTime? DomainVercelAddedAt { get; set; }

    public int DomainCheckFailures { get; set; }

    [MaxLength(253)]
    public string? DomainVercelTxtHost { get; set; }

    [MaxLength(500)]
    public string? DomainVercelTxtValue { get; set; }

    [MaxLength(63)]
    public string? Subdomain { get; set; }

    // ─── Surfaces on the agency's own domain (E40/F01/US01, NF-140..NF-142, NF-149) ─────────
    // The agency's choices; what is really served is decided by Casazen.Core.Services.HostSurfaceRules (flag WhiteLabelHost, a verified
    // custom domain the org pays for, these settings). A platform subdomain never serves them.

    public bool GuestAreaOnOwnDomain { get; set; }

    public bool OwnerPortalOnOwnDomain { get; set; }

    public bool UseOwnDomainForLinks { get; set; } = true;

    [Required]
    public OwnerLoginHostState OwnerLoginHostState { get; set; } = OwnerLoginHostState.NotRequested;

    public const int GtmContainerIdMaxLength = 20;

    [MaxLength(GtmContainerIdMaxLength)]
    public string? GtmContainerId { get; set; }

    public const int AlloggiatiDelegationUserIdMaxLength = 255;

    public const int AlloggiatiDelegationTextVersionMaxLength = 50;

    /// <summary>
    /// When the org delegated the transmission of its Alloggiati communications to CasaZen, its tool (AW-R02: the agency or the host
    /// stays responsible); <c>null</c> when never granted. Written only by <c>IAlloggiatiDelegationService</c>.
    /// </summary>
    public DateTime? AlloggiatiDelegationGrantedAt { get; set; }

    [MaxLength(AlloggiatiDelegationUserIdMaxLength)]
    public string? AlloggiatiDelegationGrantedByUserId { get; set; }

    [MaxLength(AlloggiatiDelegationTextVersionMaxLength)]
    public string? AlloggiatiDelegationTextVersion { get; set; }

    public DateTime? AlloggiatiDelegationRevokedAt { get; set; }

    [MaxLength(AlloggiatiDelegationUserIdMaxLength)]
    public string? AlloggiatiDelegationRevokedByUserId { get; set; }

    public bool AutoSendGuestsToOctorate { get; set; }

    public DateTime? AutoSendGuestsToOctorateChangedAt { get; set; }

    [MaxLength(AlloggiatiDelegationUserIdMaxLength)]
    public string? AutoSendGuestsToOctorateChangedByUserId { get; set; }
}
