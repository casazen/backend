using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A payment of the agency to an owner (the beneficiary) for an approved statement version (E06/F05/US02/T03, NF-110, ADR-018): the facts the
/// withholding is decided on at the payment (the day, whether the agency collected or intervened in the payment, the qualifying short-let contract,
/// the gross components), the gross owed, the withholding decided and the net paid. Confirmed once per idempotency key: a retry finds it and never
/// decides again. A reversal keeps it and its evidence and posts the opposite lines.
/// </summary>
[Table("OwnerPayouts")]
public class OwnerPayout : ITenantOwned
{
    public const string IdempotencyIndexName = "UIX_OwnerPayouts_OrgId_IdempotencyKey";
    public const int KeyMaxLength = 100;
    public const int ReferenceMaxLength = 100;
    public const int CurrencyMaxLength = 3;
    public const int ReasonMaxLength = 500;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    /// <summary>The approved or published statement version the payout pays.</summary>
    public Guid OwnerStatementVersionId { get; set; }

    /// <summary>The key of the caller: the same key is the same payout (a retry), never a second one.</summary>
    [Required]
    [MaxLength(KeyMaxLength)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>The reference of the transfer (the wire, the bank reference).</summary>
    [MaxLength(ReferenceMaxLength)]
    public string? Reference { get; set; }

    /// <summary>The day the beneficiary is paid: the day the withholding is decided on and the period of its F24.</summary>
    public DateOnly PaidOn { get; set; }

    [Required]
    [MaxLength(CurrencyMaxLength)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>What the agency owed the owner and settles with this payout, before the withholding.</summary>
    [Precision(18, 2)]
    public decimal GrossAmount { get; set; }

    /// <summary>The withholding decided at the payment (zero when the decision is not applicable).</summary>
    [Precision(18, 2)]
    public decimal WithholdingAmount { get; set; }

    /// <summary>What was transferred: the gross minus the withholding.</summary>
    [Precision(18, 2)]
    public decimal NetAmount { get; set; }

    /// <summary>Whether the agency collected the rent or intervened in its payment; <c>null</c> = not known.</summary>
    public bool? AgencyCollected { get; set; }

    /// <summary>Whether the stays are under a qualifying short-let contract; <c>null</c> = not known.</summary>
    public bool? QualifyingShortLet { get; set; }

    /// <summary>The gross components of the payout as the agency stated them (JSON object of code to amount).</summary>
    public string? ComponentsJson { get; set; }

    /// <summary>The bookings (stays) the payout covers (JSON array of ids).</summary>
    public string? StayIdsJson { get; set; }

    public OwnerPayoutStatus Status { get; set; } = OwnerPayoutStatus.Confirmed;

    public DateTime ConfirmedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    public DateTime? ReversedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ReversedByUserId { get; set; }

    [MaxLength(ReasonMaxLength)]
    public string? ReversalReason { get; set; }

    /// <summary>The PostgreSQL <c>xmin</c> of the row.</summary>
    public uint RowVersion { get; set; }
}
