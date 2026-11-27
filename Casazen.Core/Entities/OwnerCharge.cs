using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A charge or an expense the agency debits to the owners of a property, once or every month (E06/F05/US02/T02): posted to the ledger only once approved,
/// once per month and owner (idempotent), to one owner or split by the shares in force; never edited once approved: a cancellation reverses what it
/// posted.
/// </summary>
[Table("OwnerCharges")]
public class OwnerCharge : ITenantOwned
{
    public const int DescriptionMaxLength = 300;
    public const int AttachmentMaxLength = 500;
    public const int CurrencyMaxLength = 3;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>The one owner it is debited to; <c>null</c> = split among the owners of the property by their shares on the day.</summary>
    public Guid? PropertyOwnerId { get; set; }

    public OwnerChargeKind Kind { get; set; }

    public OwnerChargeRecurrence Recurrence { get; set; }

    [Required]
    [MaxLength(DescriptionMaxLength)]
    public string Description { get; set; } = string.Empty;

    /// <summary>The amount of each occurrence, positive (debited).</summary>
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string Currency { get; set; } = string.Empty;

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    [MaxLength(AttachmentMaxLength)]
    public string? AttachmentReference { get; set; }

    public OwnerChargeStatus Status { get; set; } = OwnerChargeStatus.Pending;

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ApprovedByUserId { get; set; }

    public DateTime? CancelledAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CancelledByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
