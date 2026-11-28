using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One rent installment of a lease (LT-06, #269): the period it pays, its due date and amount, and its real payment
/// state. Generated from the lease's <see cref="RentSchedule"/> (<see cref="Leases.RentInstallmentPlan"/>); paid online by
/// the tenant on the landlord's Stripe connected account (state from the webhooks), or declared paid offline by the
/// landlord. Never "paid" without one of the two.
/// </summary>
[Table("RentLedgerEntries")]
public class RentLedgerEntry : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid LeaseContractId { get; set; }

    public Guid RentScheduleId { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public DateOnly DueDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountDue { get; set; }

    public RentLedgerStatus Status { get; set; } = RentLedgerStatus.Scheduled;

    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    [MaxLength(255)]
    public string? ConnectedAccountId { get; set; }

    /// <summary>PaymentIntents created for the installment so far: part of the idempotency key of the next one.</summary>
    public int PaymentIntentCount { get; set; }

    [MaxLength(64)]
    public string? PaymentTokenHash { get; set; }

    public DateTime? PaymentRequestedAt { get; set; }

    /// <summary>
    /// When the landlord last reminded the tenants of this installment (LR-01, B1); null if never. The next reminder is possible
    /// <c>RentBilling:ReminderIntervalHours</c> after it (<see cref="Services.RentCharges.GetReminderIntervalHours"/>). Set under the
    /// lock of the lease, only when the email was queued.
    /// </summary>
    public DateTime? LastReminderAt { get; set; }

    public int ReminderCount { get; set; }

    [MaxLength(100)]
    public string? FailureCode { get; set; }

    public DateTime? LastFailedAt { get; set; }

    public RentPaymentChannel? PaidVia { get; set; }

    public DateOnly? PaidOn { get; set; }

    [MaxLength(200)]
    public string? MarkedPaidByUserId { get; set; }

    [MaxLength(500)]
    public string? OfflinePaymentNote { get; set; }

    public bool IsVatExempt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal StampDutyAmount { get; set; }

    [MaxLength(1000)]
    public string? ReceiptStoragePath { get; set; }

    public DateTime? ChargedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TransactionFee { get; set; }
    public DateTime? SepaAutomaticAttemptedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public RentSchedule RentSchedule { get; set; } = null!;
    public LeaseContract LeaseContract { get; set; } = null!;
    public Org Org { get; set; } = null!;

    public ICollection<RentLedgerComponent> Components { get; set; } = [];
}
