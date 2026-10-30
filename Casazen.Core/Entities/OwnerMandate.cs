using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Defines the commission and settlement terms between a property manager and an owner
/// for a specific PropertyOwnership (ADR-012-R8). The mandate covers commission rates,
/// flat-rate services, withholding treatment, and settlement frequency.
/// Multiple mandates per PropertyOwnership preserve history (EffectiveFrom/EffectiveUntil).
/// </summary>
[Table("OwnerMandates")]
public class OwnerMandate : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (ADR-012-R8). Inherited from PropertyOwnership; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>
    /// The PropertyOwnership this mandate applies to. One ownership may have multiple mandates
    /// over time (history preserved via EffectiveFrom/EffectiveUntil).
    /// </summary>
    public Guid PropertyOwnershipId { get; set; }
    public virtual PropertyOwnership PropertyOwnership { get; set; } = null!;

    /// <summary>
    /// Commission percentage charged by the property manager on gross rental revenue.
    /// Stored as decimal(5,2) in PostgreSQL (e.g., 15.00 = 15%).
    /// </summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal CommissionPercent { get; set; }

    /// <summary>
    /// Optional flat rate charged per stay for cleaning services (EUR).
    /// Null = cleaning fees are passed through to the owner without markup.
    /// Stored as decimal(10,2) in PostgreSQL.
    /// </summary>
    [Column(TypeName = "decimal(10,2)")]
    public decimal? CleaningFlatRate { get; set; }

    /// <summary>
    /// Optional flat rate charged per stay for linen services (EUR).
    /// Null = linen fees are passed through to the owner without markup.
    /// Stored as decimal(10,2) in PostgreSQL.
    /// </summary>
    [Column(TypeName = "decimal(10,2)")]
    public decimal? LinenFlatRate { get; set; }

    /// <summary>
    /// Whether payment processing fees (Stripe) are passed through to the owner (true)
    /// or absorbed by the property manager (false). Default: false.
    /// </summary>
    public bool PassthroughPaymentFees { get; set; }

    /// <summary>
    /// Tax treatment of the commission for the owner's tax calculation (ADR-012).
    /// Deductible = commission reduces taxable income; NotDeductible = owner pays tax on gross revenue.
    /// </summary>
    public CommissionTreatment WithholdingTreatment { get; set; }

    /// <summary>
    /// Settlement frequency in months (ADR-012-R8).
    /// 1 = monthly settlements (default and most common).
    /// Other values (e.g., 3 = quarterly) may be supported in future.
    /// </summary>
    public int MonthlyFrequency { get; set; } = 1;

    /// <summary>
    /// URL of the signed mandate PDF stored in blob storage (ADR-012-R8).
    /// The PDF is the legal contract between property manager and owner.
    /// Required before the first settlement can be generated.
    /// </summary>
    [Required, MaxLength(500)]
    public string MandatePdfUrl { get; set; } = string.Empty;

    /// <summary>
    /// When this mandate becomes effective (UTC). Used to select the correct mandate
    /// when generating a settlement for a given period.
    /// </summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>
    /// When this mandate ends (UTC). Null = currently active mandate.
    /// Used to preserve history when terms change or the mandate is terminated.
    /// </summary>
    public DateTime? EffectiveUntil { get; set; }
}
