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

    /// <summary>Calendar day (Europe/Rome) by which the installment is due.</summary>
    public DateOnly DueDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountDue { get; set; }

    public RentLedgerStatus Status { get; set; } = RentLedgerStatus.Scheduled;

    /// <summary>Current PaymentIntent of the installment on <see cref="ConnectedAccountId"/>; null before the tenant starts paying.</summary>
    [MaxLength(255)]
    public string? StripePaymentIntentId { get; set; }

    [MaxLength(255)]
    public string? ConnectedAccountId { get; set; }

    /// <summary>PaymentIntents created for the installment so far: part of the idempotency key of the next one.</summary>
    public int PaymentIntentCount { get; set; }

    /// <summary>SHA-256 of the token of the tenant's payment link (<see cref="Services.CheckoutOutcomes.HashToken"/>); the raw token is only in the email.</summary>
    [MaxLength(64)]
    public string? PaymentTokenHash { get; set; }

    /// <summary>When the payment link was last emailed to the tenant(s).</summary>
    public DateTime? PaymentRequestedAt { get; set; }

    /// <summary>
    /// When the landlord last reminded the tenants of this installment (LR-01, B1); null if never. The next reminder is possible
    /// <c>RentBilling:ReminderIntervalHours</c> after it (<see cref="Services.RentCharges.GetReminderIntervalHours"/>). Set under the
    /// lock of the lease, only when the email was queued.
    /// </summary>
    public DateTime? LastReminderAt { get; set; }

    /// <summary>How many reminders the landlord sent for this installment (LR-01); each one is at least the interval after the previous.</summary>
    public int ReminderCount { get; set; }

    /// <summary>Stripe error code of the last failed online payment (e.g. <c>card_declined</c>); no personal data.</summary>
    [MaxLength(100)]
    public string? FailureCode { get; set; }

    public DateTime? LastFailedAt { get; set; }

    public RentPaymentChannel? PaidVia { get; set; }

    /// <summary>Day the installment was paid: the Rome date of the Stripe success, or the date declared by the landlord.</summary>
    public DateOnly? PaidOn { get; set; }

    /// <summary>User who declared the offline payment.</summary>
    [MaxLength(200)]
    public string? MarkedPaidByUserId { get; set; }

    /// <summary>Optional note of the offline payment (e.g. "bonifico del 3/10"), typed by the landlord.</summary>
    [MaxLength(500)]
    public string? OfflinePaymentNote { get; set; }

    public bool IsVatExempt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal StampDutyAmount { get; set; }

    [MaxLength(1000)]
    public string? ReceiptStoragePath { get; set; }

    /// <summary>When the first PaymentIntent of the installment was created (the tenant started paying online).</summary>
    public DateTime? ChargedAt { get; set; }

    /// <summary>When the payment was recorded (webhook or landlord declaration).</summary>
    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public RentSchedule RentSchedule { get; set; } = null!;
    public LeaseContract LeaseContract { get; set; } = null!;
    public Org Org { get; set; } = null!;
}
