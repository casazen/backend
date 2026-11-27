using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Condominium terms of one lease (CondoLedger phase 1): the advance the contract states, whether a settlement is due,
/// and who confirmed the values. There is one current row per lease. Periods, statements and allocations are a later module.
/// </summary>
[Table("LeaseCondominiumTerms")]
public class LeaseCondominiumTerms : ITenantOwned
{
    public const int ProrationRuleMaxLength = 200;
    public const int ConfirmedByUserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public CondominiumAdvanceMode AdvanceMode { get; set; }

    /// <summary>Advance of one period of <see cref="Cadence"/>. Required when <see cref="AdvanceMode"/> is the fixed contractual amount.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? AdvanceAmount { get; set; }

    /// <summary>How often <see cref="AdvanceAmount"/> is due. The rent plan scales it onto its own cadence when the two divide evenly.</summary>
    public RentCadence? Cadence { get; set; }

    public bool SettlementRequired { get; set; }

    [MaxLength(ProrationRuleMaxLength)]
    public string? ProrationRule { get; set; }

    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    /// <summary>The lease document the values were read from. Cleared if that document is deleted.</summary>
    public Guid? SourceDocumentId { get; set; }

    /// <summary>Confidence of the extraction, from 0 to 1. Below 0.85 nothing is proposed as a charge until a person confirms.</summary>
    [Column(TypeName = "decimal(5,4)")]
    public decimal? ExtractionConfidence { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(ConfirmedByUserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public LeaseContract LeaseContract { get; set; } = null!;

    public LeaseDocument? SourceDocument { get; set; }

    public Org Org { get; set; } = null!;
}
