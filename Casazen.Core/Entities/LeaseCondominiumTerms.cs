using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

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

    [Column(TypeName = "decimal(18,2)")]
    public decimal? AdvanceAmount { get; set; }

    public RentCadence? Cadence { get; set; }

    public bool SettlementRequired { get; set; }

    [MaxLength(ProrationRuleMaxLength)]
    public string? ProrationRule { get; set; }

    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public Guid? SourceDocumentId { get; set; }

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
