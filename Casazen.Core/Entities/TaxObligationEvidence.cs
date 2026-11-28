using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// The withholding decision taken at a payout (E06/F05/US02/T03, NF-110, AC-NF110-4, AC-NF110-5, AC-NF110-7), append-only
/// (<see cref="IAppendOnlyRecord"/>): applicable, with the gross base, the rate, the rounding, the amount and the tax code of the approved rule, or
/// explicitly not applicable with its reason (never a zero-value obligation). It links the payout, the stays, the statement version and the rule
/// version; a reversal is a new row (<see cref="ReversesEvidenceId"/>) with the opposite amounts.
/// </summary>
[Table("TaxObligationEvidence")]
public class TaxObligationEvidence : ITenantOwned, IAppendOnlyRecord
{
    public const string IdempotencyIndexName = "UIX_TaxObligationEvidence_OrgId_IdempotencyKey";
    public const string ReversalIndexName = "UIX_TaxObligationEvidence_ReversesEvidenceId";
    public const int KeyMaxLength = 150;
    public const int CodeMaxLength = 64;
    public const int JurisdictionMaxLength = 8;
    public const int RuleMaxLength = 80;
    public const int TaxCodeMaxLength = 32;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid OwnerPayoutId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    public Guid OwnerStatementVersionId { get; set; }

    [Required]
    [MaxLength(KeyMaxLength)]
    public string IdempotencyKey { get; set; } = string.Empty;

    public TaxDecision Decision { get; set; }

    [Required]
    [MaxLength(CodeMaxLength)]
    public string ReasonCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(JurisdictionMaxLength)]
    public string Jurisdiction { get; set; } = string.Empty;

    [Required]
    [MaxLength(RuleMaxLength)]
    public string RuleCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(RuleMaxLength)]
    public string RuleVersion { get; set; } = string.Empty;

    [Precision(18, 2)]
    public decimal? GrossBase { get; set; }

    [Precision(7, 4)]
    public decimal? RatePercent { get; set; }

    [MaxLength(CodeMaxLength)]
    public string? Rounding { get; set; }

    [Precision(18, 2)]
    public decimal? Amount { get; set; }

    [MaxLength(TaxCodeMaxLength)]
    public string? TaxCode { get; set; }

    public DateOnly PaidOn { get; set; }

    public int PeriodYear { get; set; }

    public int PeriodMonth { get; set; }

    public string? StayIdsJson { get; set; }

    [Required]
    public string FactsJson { get; set; } = "{}";

    public Guid? ReversesEvidenceId { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
