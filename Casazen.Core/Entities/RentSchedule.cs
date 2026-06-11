using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("RentSchedules")]
public class RentSchedule
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public RentCadence Cadence { get; set; } = RentCadence.Monthly;

    public int BillingDayOfMonth { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "eur";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

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
