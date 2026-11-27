using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A budget or final read from an administrator PDF. It does not change the book until someone confirms it.
/// </summary>
[Table("CondominiumStatementProposals")]
public class CondominiumStatementProposal : ITenantOwned
{
    public const int ContentHashMaxLength = 64;
    public const int LabelMaxLength = 20;
    public const int DescriptionMaxLength = 200;
    public const int ExcerptMaxLength = 500;
    public const int FileNameMaxLength = 200;
    public const int UserIdMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid CondominiumId { get; set; }

    [Required]
    [MaxLength(ContentHashMaxLength)]
    public string ContentHash { get; set; } = string.Empty;

    [MaxLength(FileNameMaxLength)]
    public string? FileName { get; set; }

    public CondominiumStatementKind Kind { get; set; }

    [Required]
    [MaxLength(LabelMaxLength)]
    public string Label { get; set; } = string.Empty;

    public Guid? PeriodId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? OrdinaryAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? ExtraordinaryAmount { get; set; }

    [MaxLength(DescriptionMaxLength)]
    public string? ExtraordinaryDescription { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? FinalAmount { get; set; }

    public PriorYearBalanceDisposition? Settlement { get; set; }

    [Column(TypeName = "decimal(5,4)")]
    public decimal Confidence { get; set; }

    /// <summary>False when the reading is uncertain: the confirmed line stays <see cref="TenantRecoverability.NeedsReview"/>.</summary>
    public bool ChargesTenant { get; set; }

    [MaxLength(ExcerptMaxLength)]
    public string Excerpt { get; set; } = string.Empty;

    public CondominiumStatementProposalStatus Status { get; set; } = CondominiumStatementProposalStatus.Proposed;

    public Guid? StatementId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ConfirmedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    public Condominium Condominium { get; set; } = null!;

    public Org Org { get; set; } = null!;
}
