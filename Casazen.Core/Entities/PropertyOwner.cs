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

    /// <summary>When the owner was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the owner's personal data was anonymized for GDPR (UTC). Null while not anonymized.
    /// Fiscal data may be retained per fiscal retention rules (ADR-012-R6).
    /// </summary>
    public DateTime? AnonymizedAt { get; set; }
}
