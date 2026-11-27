using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One consuntivo line shared onto a lease. A line in review, or an extraordinary line nobody confirmed, stays excluded.
/// </summary>
[Table("TenantExpenseAllocations")]
public class TenantExpenseAllocation : ITenantOwned
{
    public const int UserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public Guid TenantCondominiumSettlementId { get; set; }

    public Guid CondominiumExpenseLineId { get; set; }

    public TenantAllocationRule Rule { get; set; }

    public DateOnly? OccupancyStart { get; set; }

    public DateOnly? OccupancyEnd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal BaseAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AllocatedAmount { get; set; }

    public string CalculationDetailsJson { get; set; } = "{}";

    public TenantAllocationStatus Status { get; set; } = TenantAllocationStatus.Proposed;

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    public TenantCondominiumSettlement Settlement { get; set; } = null!;

    public LeaseContract LeaseContract { get; set; } = null!;

    public CondominiumExpenseLine ExpenseLine { get; set; } = null!;

    public Org Org { get; set; } = null!;
}
