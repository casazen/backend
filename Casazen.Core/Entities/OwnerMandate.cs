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
    /// <summary>Unique (<see cref="OrgId"/>, <see cref="PropertyOwnerId"/>, <see cref="ValidFrom"/>) among the mandates of the owner (no ownership).</summary>
    public const string OwnerValidFromIndexName = "UIX_OwnerMandates_OrgId_PropertyOwnerId_ValidFrom";

    /// <summary>The longest <see cref="MandatePdfUrl"/>.</summary>
    public const int DocumentMaxLength = 500;

    /// <summary>The longest <see cref="CreatedByUserId"/>.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (ADR-012-R8). The owner's org; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The owner the mandate is of (E13/F01/US01/T01). Restricts the deletion of the owner: a mandate is evidence of the terms that applied.</summary>
    public Guid PropertyOwnerId { get; set; }

    /// <summary>
    /// The one ownership a mandate of before E13 covers; <c>null</c> for the mandates of the owner, which cover all its properties (the only ones the
    /// API writes). A mandate of an ownership wins over the owner's for that ownership.
    /// </summary>
    public Guid? PropertyOwnershipId { get; set; }
    public virtual PropertyOwnership? PropertyOwnership { get; set; }

    /// <summary>The commission of the agency in percent, 0 to 100 with two decimals (decimal(5,2): 15.00 = 15%).</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal CommissionPercent { get; set; }

    /// <summary>What <see cref="CommissionPercent"/> is a percentage of: the gross of the stay without the tourist tax by default (⚖ R3).</summary>
    public CommissionBase CommissionBase { get; set; } = CommissionBase.GrossExclTouristTax;

    /// <summary>
    /// Whether the percent and the flat fees already include VAT. <c>false</c> by default (⚖ R4): they are «+ IVA», and the owner invoice (E14) adds the
    /// VAT; the ledger posts the amounts as they are written here.
    /// </summary>
    public bool CommissionIncludesVat { get; set; }

    /// <summary>
    /// The flat fee of the cleaning of a stay, in the settlement currency of the owner; <c>null</c> = none. Posted once per stay as a recharge
    /// (E13/F01/US01/T02). Stored as decimal(10,2).
    /// </summary>
    [Column(TypeName = "decimal(10,2)")]
    public decimal? CleaningFlatRate { get; set; }

    /// <summary>The flat fee of the linen of a stay, in the settlement currency of the owner; <c>null</c> = none. Posted like <see cref="CleaningFlatRate"/>.</summary>
    [Column(TypeName = "decimal(10,2)")]
    public decimal? LinenFlatRate { get; set; }

    /// <summary>
    /// Whether the fees of the card payments collected through CasaZen (the Stripe commission) pass to the owner (<c>true</c>) or stay the agency's
    /// (<c>false</c>, the default).
    /// </summary>
    public bool PassthroughPaymentFees { get; set; }

    /// <summary>Tax treatment of the commission for the owner's tax calculation (ADR-012): deductible or not.</summary>
    public CommissionTreatment WithholdingTreatment { get; set; }

    /// <summary>Settlement frequency in months (ADR-012-R8): 1 = monthly, the only one the statements use.</summary>
    public int MonthlyFrequency { get; set; } = 1;

    /// <summary>
    /// Where the signed mandate is (ADR-012-R8): the key of the file in the private storage or an https address; empty while it is not on file (the
    /// readiness of the owner then says <c>mandate_document_missing</c>). The mandate is signed outside CasaZen.
    /// </summary>
    [Required, MaxLength(DocumentMaxLength)]
    public string MandatePdfUrl { get; set; } = string.Empty;

    /// <summary>The first day the version is in force (Europe/Rome).</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>The last day the version is in force (Europe/Rome, included); <c>null</c> = until a later version.</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>When the version was written (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Who wrote it; <c>null</c> for a mandate of before E13.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
