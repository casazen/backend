using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The terms between the agency and an owner (ADR-012-R8, E13/F01/US01/T01): the commission of the agency, what it is a percentage of, the flat fees of a
/// stay, whether the payment fees pass to the owner, the withholding treatment and the settlement frequency. A mandate is a <b>version</b> in force from
/// <see cref="ValidFrom"/> to <see cref="ValidTo"/> (both included, Rome days): a new version of an owner ends the one in force the day before it starts, so
/// the versions of an owner never overlap, as the fiscal profiles do.
/// </summary>
/// <remarks>
/// <para><b>Of the owner.</b> The mandates the API writes cover every property of the owner (<see cref="PropertyOwnershipId"/> <c>null</c>). A mandate of
/// one ownership (<see cref="PropertyOwnershipId"/> set) is the shape of before E13 that nothing writes any more: it is still read, and for its ownership it
/// wins over the owner's (<c>OwnerMandateRules.InForce</c>, the one definition every reader uses).</para>
/// <para><b>Money.</b> The commission is posted on the owner's share of a stay; the flat fees are per stay in the owner's settlement currency; none of
/// them includes VAT unless <see cref="CommissionIncludesVat"/> says so (⚖ R4), the VAT is the owner invoice's (E14).</para>
/// </remarks>
[Table("OwnerMandates")]
public class OwnerMandate : ITenantOwned
{
    public const string OwnerValidFromIndexName = "UIX_OwnerMandates_OrgId_PropertyOwnerId_ValidFrom";

    public const int DocumentMaxLength = 500;

    public const int UserIdMaxLength = 255;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid PropertyOwnerId { get; set; }

    public Guid? PropertyOwnershipId { get; set; }
    public virtual PropertyOwnership? PropertyOwnership { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal CommissionPercent { get; set; }

    public CommissionBase CommissionBase { get; set; } = CommissionBase.GrossExclTouristTax;

    /// <summary>
    /// Whether the percent and the flat fees already include VAT. <c>false</c> by default (⚖ R4): they are «+ IVA», and the owner invoice (E14) adds the
    /// VAT; the ledger posts the amounts as they are written here.
    /// </summary>
    public bool CommissionIncludesVat { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal? CleaningFlatRate { get; set; }

    public CleaningRechargeMode? CleaningRechargeMode { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal? LinenFlatRate { get; set; }

    public bool PassthroughPaymentFees { get; set; }

    public CommissionTreatment WithholdingTreatment { get; set; }

    /// <summary>
    /// How the withholding evidence and the CU of a property held by several co-owners are written (E18/F01/US03/T01, ⚖ I6): <see cref="CoOwnerTaxReporting.Signatory"/> by
    /// default (everything on the co-owner who signed the mandate: «altrimenti sul firmatario»), or <see cref="CoOwnerTaxReporting.PerShare"/> (split by the share of each
    /// co-owner). Read by the withholding facts and by the annual tax data; every mandate written before this field is <see cref="CoOwnerTaxReporting.Signatory"/>.
    /// </summary>
    public CoOwnerTaxReporting CoOwnerTaxReporting { get; set; } = CoOwnerTaxReporting.Signatory;

    public int MonthlyFrequency { get; set; } = 1;

    [Required, MaxLength(DocumentMaxLength)]
    public string MandatePdfUrl { get; set; } = string.Empty;

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
