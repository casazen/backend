using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Money the owner paid the condominium (CondoLedger <c>payments</c>). <see cref="ExternalSourceHash"/> stops the same
/// imported movement from being recorded twice. This is not a tenant rent payment.
/// </summary>
[Table("CondominiumPayments")]
public class CondominiumPayment : ITenantOwned
{
    public const int MethodMaxLength = 50;
    public const int NoteMaxLength = 500;
    public const int ExternalSourceHashMaxLength = 128;
    public const int UserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? CondominiumInstallmentId { get; set; }

    public Guid CondominiumPeriodId { get; set; }

    /// <summary>Set when the payment covers the prior-year balance rather than a rate.</summary>
    public Guid? PriorBalanceId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateOnly PaidOn { get; set; }

    [MaxLength(MethodMaxLength)]
    public string? Method { get; set; }

    public Guid? BankMovementId { get; set; }

    [MaxLength(ExternalSourceHashMaxLength)]
    public string? ExternalSourceHash { get; set; }

    [MaxLength(NoteMaxLength)]
    public string? Note { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? RecordedByUserId { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public CondominiumInstallment? Installment { get; set; }

    public CondominiumPeriod Period { get; set; } = null!;

    public CondominiumPriorBalance? PriorBalance { get; set; }

    public Org Org { get; set; } = null!;
}
