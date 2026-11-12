using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The tenant's settlement of one condominium year. The snapshot replays the arithmetic even if the rules change later.
/// A new consuntivo supersedes a posted row and posts another. Nothing here is emailed on its own.
/// </summary>
[Table("TenantCondominiumSettlements")]
public class TenantCondominiumSettlement : ITenantOwned
{
    public const int UserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public Guid CondominiumPeriodId { get; set; }

    public Guid StatementId { get; set; }

    public int StatementRevision { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RecoverableFinalAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AdvancesAccountedAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PriorTenantBalanceAmount { get; set; }

    /// <summary>Signed euros. Positive is a tenant debit, negative a credit, zero is even.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal SettlementAmount { get; set; }

    public TenantSettlementDirection Direction { get; set; }

    public TenantSettlementDisposition Disposition { get; set; }

    public TenantAllocationRule AllocationRule { get; set; }

    public TenantSettlementStatus Status { get; set; } = TenantSettlementStatus.Draft;

    /// <summary>The prior carry-forward row this settlement absorbed, once posted. Null until then.</summary>
    public Guid? AbsorbedBySettlementId { get; set; }

    public string CalculationSnapshotJson { get; set; } = "{}";

    /// <summary>Set only when a person records that the settlement was sent. Posting never sets it.</summary>
    public DateTime? SentToTenantAt { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    public DateTime? PostedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? PostedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public LeaseContract LeaseContract { get; set; } = null!;

    public CondominiumPeriod Period { get; set; } = null!;

    public Org Org { get; set; } = null!;

    public ICollection<TenantExpenseAllocation> Allocations { get; set; } = [];
}
