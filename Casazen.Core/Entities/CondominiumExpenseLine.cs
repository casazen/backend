using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One amount on a statement. Ordinary and extraordinary stay on different lines. An uncertain line is not a tenant charge.
/// </summary>
[Table("CondominiumExpenseLines")]
public class CondominiumExpenseLine : ITenantOwned
{
    public const int DescriptionMaxLength = 500;
    public const int CategoryMaxLength = 100;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid StatementId { get; set; }

    public Guid? CondominiumUnitId { get; set; }

    [Required]
    [MaxLength(DescriptionMaxLength)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(CategoryMaxLength)]
    public string? Category { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateOnly CompetenceStart { get; set; }

    public DateOnly CompetenceEnd { get; set; }

    public CondominiumExpenseNature ExpenseNature { get; set; }

    public TenantRecoverability TenantRecoverability { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? RecoverablePercentage { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RecoverableAmount { get; set; }

    public ExpenseClassificationSource ClassificationSource { get; set; }

    /// <summary>0 to 1. Below 0.85 the line is not charged automatically.</summary>
    [Column(TypeName = "decimal(5,4)")]
    public decimal? Confidence { get; set; }

    public ExpenseReviewStatus ReviewStatus { get; set; }

    public CondominiumStatement Statement { get; set; } = null!;

    public CondominiumUnit? Unit { get; set; }

    public Org Org { get; set; } = null!;
}
