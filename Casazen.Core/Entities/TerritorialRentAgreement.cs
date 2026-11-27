using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("TerritorialRentAgreements")]
public class TerritorialRentAgreement
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(100)]
    public string Comune { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Region { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string AgreementName { get; set; } = string.Empty;

    public DateTime SignedDate { get; set; }

    public DateTime EffectiveDate { get; set; }

    [MaxLength(500)]
    public string SourceUrl { get; set; } = string.Empty;

    public DataCompleteness DataCompleteness { get; set; } = DataCompleteness.Missing;

    public DateTime? LastVerifiedAt { get; set; }

    [MaxLength(500)]
    public string? VerificationSource { get; set; }

    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// True when the agreement's own text keeps it in force past <see cref="ExpiresAt"/> until the signatories sign a
    /// new one (common clause on Italian canone concordato agreements). A range past the expiry is then only flagged
    /// (<c>agreement_expired</c> warning), never blocked.
    /// </summary>
    public bool RemainsInForceUntilReplaced { get; set; }

    [MaxLength(500)]
    public string? ExpiryNote { get; set; }

    /// <summary>Last admin edit (fields or verification); null for a row never touched since the seed.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>A-elements the unit must all have, otherwise sub-fascia 1.</summary>
    public int RequiredTypeACount { get; set; } = 2;

    public int SubFascia2MinTypeBCount { get; set; }

    public int SubFascia3MinTypeCCount { get; set; }

    public int SubFascia3MinQualifyingTypeDCount { get; set; }

    [MaxLength(100)]
    public string? SubFascia3QualifyingTypeDElements { get; set; }

    public int SubFascia3MaxMinTypeDCount { get; set; }

    /// <summary>
    /// Heating by stoves in the single rooms puts the unit in sub-fascia 1 unless it has at least this many B-elements
    /// (MB: 4). 0: no stove rule.
    /// </summary>
    public int StoveHeatingMinTypeBCount { get; set; }

    public CoefficientCombination CoefficientCombination { get; set; } = CoefficientCombination.Additive;

    [Precision(5, 2)]
    public decimal FurnishedUpliftPercent { get; set; }

    [Precision(5, 2)]
    public decimal AirConditioningUpliftPercent { get; set; }

    public int SmallSqmMax { get; set; }

    [Precision(5, 2)]
    public decimal SmallSqmUpliftPercent { get; set; }

    public int MidSqmMin { get; set; }

    public int MidSqmMax { get; set; }

    [Precision(5, 2)]
    public decimal MidSqmUpliftPercent { get; set; }

    public int LargeSqmMin { get; set; }

    [Precision(5, 2)]
    public decimal LargeSqmReductionPercent { get; set; }

    [Precision(5, 2)]
    public decimal GarageAppurtenancePercent { get; set; }

    [Precision(5, 2)]
    public decimal BalconyAppurtenancePercent { get; set; }

    [Precision(5, 2)]
    public decimal OtherAppurtenancePercent { get; set; }

    [Precision(5, 2)]
    public decimal GreenAreaAppurtenancePercent { get; set; }

    [Precision(5, 2)]
    public decimal Duration4UpliftPercent { get; set; }

    [Precision(5, 2)]
    public decimal Duration5UpliftPercent { get; set; }

    [Precision(5, 2)]
    public decimal Duration6UpliftPercent { get; set; }

    public ICollection<ConcordatoRentBand> Bands { get; set; } = new List<ConcordatoRentBand>();

    public ICollection<TerritorialAgreementSignatory> Signatories { get; set; } = new List<TerritorialAgreementSignatory>();
}
