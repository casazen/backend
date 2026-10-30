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

    /// <summary>
    /// Whether the daily job emails the tenants a payment link for the installments coming due (LR-04a, decision LR-D19).
    /// Tracking the rents and asking for them online are two different things: a plan created without saying so is
    /// <b>off</b> (the landlord turns it on, or sends the link of one installment by hand), and the plans that existed
    /// when the column was added are <b>on</b>, as they always were. Only the job reads it: the landlord's own payment
    /// request, the link, the webhooks and the offline payment do not depend on it.
    /// </summary>
    public bool OnlineRequestsEnabled { get; set; }

    /// <summary>
    /// The day the rents are tracked from (LR-04a, decision LR-D17): no installment is generated for a period that ended
    /// before it, so a lease that started long ago does not get years of installments «in ritardo». Null = from the start
    /// of the lease. Kept with the plan so that every later builder of its installments (saving the plan again, the tacit
    /// renewal of the lease) stops at the same day.
    /// </summary>
    public DateOnly? TrackFrom { get; set; }

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
