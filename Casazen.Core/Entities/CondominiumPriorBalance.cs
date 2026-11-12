using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The balance brought into this fiscal year from the previous one (CondoLedger <c>prior_balances</c>).
/// One row per year. Positive is a debit, negative a credit. It is not folded into the ordinary quota.
/// </summary>
[Table("CondominiumPriorBalances")]
public class CondominiumPriorBalance : ITenantOwned
{
    public const int DescriptionMaxLength = 500;
    public const int UserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid CondominiumPeriodId { get; set; }

    public Guid? SourcePeriodId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public PriorYearBalanceDisposition Disposition { get; set; }

    [MaxLength(DescriptionMaxLength)]
    public string? Description { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? DecidedByUserId { get; set; }

    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;

    public CondominiumPeriod Period { get; set; } = null!;

    public CondominiumPeriod? SourcePeriod { get; set; }

    public Org Org { get; set; } = null!;
}
