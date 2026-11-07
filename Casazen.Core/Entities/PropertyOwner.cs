using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Property owner in the agency registry (ADR-012): the natural or legal person who owns a property managed
/// by the agency. Separate from <c>OwnerUser</c> (portal access): a property owner is not necessarily a user.
/// IBAN and fiscal code are encrypted at rest with the <c>PropertyOwnerEncryptionPurpose</c>.
/// </summary>
[Table("PropertyOwners")]
public class PropertyOwner : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (ADR-012-R7). Inherited from the agency org; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Phone { get; set; }

    /// <summary>
    /// Italian fiscal code (codice fiscale). Encrypted at rest (ADR-012-R1) with <c>PropertyOwnerEncryptionPurpose</c>.
    /// NEVER appears in logs, error responses or activity log (ADR-012-R2).
    /// </summary>
    [MaxLength(500)]
    public string? FiscalCode { get; set; }

    /// <summary>
    /// IBAN for agency statement payments. Encrypted at rest (ADR-012-R1) with <c>PropertyOwnerEncryptionPurpose</c>.
    /// NEVER appears in logs, error responses or activity log (ADR-012-R2). List endpoint returns masked form;
    /// full IBAN requires <c>agency.owner.financial.read</c> permission and is logged (ADR-012-R3).
    /// </summary>
    [MaxLength(500)]
    public string? Iban { get; set; }

    /// <summary>Tax regime: private natural person (withholding tax) or VAT holder (invoice).</summary>
    public OwnerTaxRegime TaxRegime { get; set; } = OwnerTaxRegime.Private;

    /// <summary>
    /// The key the agency gives the owner in its own system (E10/F02/US02/T01): the first thing the owner import matches on. Unique inside the
    /// org (a filtered unique index, <c>null</c> for an owner without one). Not a secret and not personal data: an agency code such as
    /// <c>PRO-0042</c>.
    /// </summary>
    [MaxLength(ExternalKeyMaxLength)]
    public string? ExternalKey { get; set; }

    /// <summary>
    /// The legal name as it appears on the owner's fiscal documents: the full name of a person, the name of a company. <c>FirstName</c> and
    /// <c>LastName</c> remain the display name of the screens that existed before the registry had a fiscal profile.
    /// </summary>
    [MaxLength(LegalNameMaxLength)]
    public string? LegalName { get; set; }

    /// <summary>Natural person or legal entity; <c>null</c> until declared (an owner of before the import).</summary>
    public OwnerEntityKind? EntityKind { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country of the tax residence (<c>IT</c>, <c>CH</c>); <c>null</c> until declared.</summary>
    [MaxLength(2)]
    public string? TaxResidenceCountry { get; set; }

    /// <summary>
    /// VAT number, as the owner gave it. Encrypted at rest with the <c>PropertyOwnerEncryptionPurpose</c> like the fiscal code (ADR-012-R1);
    /// NEVER in logs, error responses, the activity log or an answer of the onboarding (ADR-012-R2).
    /// </summary>
    [MaxLength(500)]
    public string? VatNumber { get; set; }

    /// <summary>
    /// BIC/SWIFT of the bank of <see cref="Iban"/>, needed when the IBAN is outside the SEPA area. Encrypted at rest like the IBAN;
    /// NEVER in logs or answers.
    /// </summary>
    [MaxLength(500)]
    public string? Bic { get; set; }

    /// <summary>ISO 4217 currency the owner is paid in (<c>EUR</c>, <c>CHF</c>); <c>null</c> until declared.</summary>
    [MaxLength(3)]
    public string? SettlementCurrency { get; set; }

    /// <summary>Structured billing address: the street (without the civic number).</summary>
    [MaxLength(200)]
    public string? BillingStreet { get; set; }

    /// <summary>Structured billing address: the civic number.</summary>
    [MaxLength(20)]
    public string? BillingCivic { get; set; }

    /// <summary>Structured billing address: the postal code.</summary>
    [MaxLength(20)]
    public string? BillingPostalCode { get; set; }

    /// <summary>Structured billing address: the city.</summary>
    [MaxLength(100)]
    public string? BillingCity { get; set; }

    /// <summary>Structured billing address: the province (the two-letter sigla in Italy) or the canton/region abroad.</summary>
    [MaxLength(50)]
    public string? BillingProvince { get; set; }

    /// <summary>Structured billing address: ISO 3166-1 alpha-2 country.</summary>
    [MaxLength(2)]
    public string? BillingCountry { get; set; }

    /// <summary>The longest <see cref="ExternalKey"/>.</summary>
    public const int ExternalKeyMaxLength = 64;

    /// <summary>The longest <see cref="LegalName"/>.</summary>
    public const int LegalNameMaxLength = 200;

    /// <summary>The name of the filtered unique index on the org and the key of the owner.</summary>
    public const string OrgExternalKeyIndexName = "UIX_PropertyOwners_OrgId_ExternalKey";

    /// <summary>When the owner was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the owner's personal data was anonymized for GDPR (UTC). Null while not anonymized.
    /// Fiscal data may be retained per fiscal retention rules (ADR-012-R6).
    /// </summary>
    public DateTime? AnonymizedAt { get; set; }
}
