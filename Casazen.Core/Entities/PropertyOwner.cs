using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Property owner in the agency registry (ADR-012): the natural or legal person who owns a property managed
/// by the agency. Separate from <c>OwnerUser</c> (portal access): a property owner is not necessarily a user.
/// IBAN, BIC, fiscal code, VAT number and the structured billing address are encrypted at rest with the <c>PropertyOwnerEncryptionPurpose</c>.
/// </summary>
[Table("PropertyOwners")]
public class PropertyOwner : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

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

    [MaxLength(500)]
    public string? Iban { get; set; }

    public OwnerTaxRegime TaxRegime { get; set; } = OwnerTaxRegime.Private;

    [MaxLength(ExternalKeyMaxLength)]
    public string? ExternalKey { get; set; }

    [MaxLength(LegalNameMaxLength)]
    public string? LegalName { get; set; }

    public OwnerEntityKind? EntityKind { get; set; }

    [MaxLength(2)]
    public string? TaxResidenceCountry { get; set; }

    /// <summary>
    /// VAT number, as the owner gave it. Encrypted at rest with the <c>PropertyOwnerEncryptionPurpose</c> like the fiscal code (ADR-012-R1);
    /// NEVER in logs, error responses, the activity log or an answer of the onboarding (ADR-012-R2).
    /// </summary>
    [MaxLength(500)]
    public string? VatNumber { get; set; }

    [MaxLength(500)]
    public string? Bic { get; set; }

    [MaxLength(3)]
    public string? SettlementCurrency { get; set; }

    /// <summary>
    /// How many apartments the owner lets short outside the agency, as the owner declared them (E18/F01/US03/T01, ⚖ I8, NF-117): they count with the properties the
    /// agency manages for the owner towards the limit of units per taxpayer (<c>ShortStayFiscal:MaxApartmentsPerTaxpayer</c>), beyond which the activity is presumed to
    /// be a business. 0 when nothing was declared; at most <see cref="ExternalShortLetUnitsMax"/>. A count, never personal data.
    /// </summary>
    public int ExternalShortLetUnits { get; set; }

    public const int ExternalShortLetUnitsMax = 1_000;

    // The structured billing address (E13/F01/US01/T01): the recipient of the owner invoice, the debtor of the QR-bill and the creditor of a pain.001.09
    // transfer. Encrypted at rest with the PropertyOwnerEncryptionPurpose, for a person as for a company (EncryptedColumns): the columns have no length (the
    // payload is longer than the text), the import and PUT api/owners/{id}/address check the length of the clear text. Never searched, never logged.

    public string? BillingStreet { get; set; }

    public string? BillingCivic { get; set; }

    public string? BillingPostalCode { get; set; }

    public string? BillingCity { get; set; }

    public string? BillingProvince { get; set; }

    public string? BillingCountry { get; set; }

    public const int BillingStreetMaxLength = 200;

    public const int BillingCivicMaxLength = 20;

    public const int BillingPostalCodeMaxLength = 20;

    public const int BillingCityMaxLength = 100;

    public const int BillingProvinceMaxLength = 50;

    public const int ExternalKeyMaxLength = 64;

    public const int LegalNameMaxLength = 200;

    public const string OrgExternalKeyIndexName = "UIX_PropertyOwners_OrgId_ExternalKey";

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the owner's personal data was anonymized for GDPR (UTC). Null while not anonymized.
    /// Fiscal data may be retained per fiscal retention rules (ADR-012-R6).
    /// </summary>
    public DateTime? AnonymizedAt { get; set; }
}
