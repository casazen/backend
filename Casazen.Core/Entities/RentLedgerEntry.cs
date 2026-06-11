using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("RentLedgerEntries")]
public class RentLedgerEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public Guid RentScheduleId { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountDue { get; set; }

    public RentLedgerStatus Status { get; set; } = RentLedgerStatus.Scheduled;

    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    [MaxLength(255)]
    public string? ConnectedAccountId { get; set; }

    public bool IsVatExempt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal StampDutyAmount { get; set; }

    [MaxLength(1000)]
    public string? ReceiptStoragePath { get; set; }

    public DateTime? ChargedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public RentSchedule RentSchedule { get; set; } = null!;
    public LeaseContract LeaseContract { get; set; } = null!;
    public Org Org { get; set; } = null!;
}
