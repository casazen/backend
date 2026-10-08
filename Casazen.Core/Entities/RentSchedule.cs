using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Rent collection plan of a lease (LT-06, #269): cadence, due day and installment amount. The installments are
/// <see cref="RentLedgerEntry"/> rows generated from it by <see cref="Leases.RentInstallmentPlan"/>.
/// </summary>
[Table("RentSchedules")]
public class RentSchedule : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public RentCadence Cadence { get; set; } = RentCadence.Monthly;

    /// <summary>Day of the month (1-28) the installment is due: the first such day on or after the start of its period.</summary>
    public int BillingDayOfMonth { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "eur";

    /// <summary>Amount of one installment (by default the lease's monthly rent times the months of the cadence).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>Due date of the first installment of the plan.</summary>
    public DateOnly NextRunDate { get; set; }

    public bool IsActive { get; set; }

    [MaxLength(255)]
    public string? LandlordStripeAccountId { get; set; }

    [MaxLength(255)]
    public string? MandateReference { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public LeaseContract LeaseContract { get; set; } = null!;
    public Org Org { get; set; } = null!;
    public ICollection<RentLedgerEntry> LedgerEntries { get; set; } = [];
}
