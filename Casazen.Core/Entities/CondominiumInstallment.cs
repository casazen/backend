using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One rate the owner owes the condominium. Ordinary, prior-year balance and extraordinary stay in their own columns
/// (CondoLedger <c>split_amounts</c>). Once issued, ordinary and extraordinary are not redistributed.
/// </summary>
[Table("CondominiumInstallments")]
public class CondominiumInstallment : ITenantOwned
{
    public const int LabelMaxLength = 80;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid CondominiumPeriodId { get; set; }

    public int SlotIndex { get; set; }

    [Required]
    [MaxLength(LabelMaxLength)]
    public string Label { get; set; } = string.Empty;

    public DateOnly PeriodStart { get; set; }

    /// <summary>Last day of the slot (CondoLedger scadenza).</summary>
    public DateOnly DueDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OrdinaryAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PriorSettlementAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ExtraordinaryAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    public CondominiumInstallmentStatus Status { get; set; } = CondominiumInstallmentStatus.Scheduled;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public CondominiumPeriod Period { get; set; } = null!;

    public Org Org { get; set; } = null!;

    public ICollection<CondominiumPayment> Payments { get; set; } = [];
}
