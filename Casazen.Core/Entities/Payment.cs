using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("Payments")]
public abstract class Payment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    [Precision(18, 2)]
    public decimal Amount { get; set; }

    [Required]
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    [Required]
    public PaymentMethod Method { get; set; } = PaymentMethod.CreditCard;

    [MaxLength(500)]
    public string TransactionId { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum PaymentStatus
{
    Pending,
    Completed,
    Failed,
    Refunded,
    PartiallyRefunded
}

public enum PaymentMethod
{
    CreditCard,
    BankTransfer,
    PayPal,
    ApplePay,
    GooglePay
}