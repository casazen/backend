using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Territorial agreement of the canone concordato for one comune, with its bands and the rules of the rent calculation
/// (LT-10). The values come from the official text of the agreement (<c>.claude/context/regulations/canone_concordato.md</c>);
/// 0 disables a rule. <see cref="DataCompleteness"/> other than Complete makes every range indicative (A7-23).
/// </summary>
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

    /// <summary>Date of the last check of the tables against the official text (RS-8: 2026-09-23 for Seveso and Cesano).</summary>
    public DateTime? LastVerifiedAt { get; set; }

    /// <summary>What <see cref="LastVerifiedAt"/> was checked against (an admin's free text, e.g. a PDF or a call log).</summary>
    [MaxLength(500)]
    public string? VerificationSource { get; set; }

    /// <summary>
    /// Formal expiry of the agreement (typically <see cref="EffectiveDate"/> plus the deposit term it states), when
    /// known. Not itself a cutoff: see <see cref="RemainsInForceUntilReplaced"/> (LT-13, A7-22).
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// True when the agreement's own text keeps it in force past <see cref="ExpiresAt"/> until the signatories sign a
    /// new one (common clause on Italian canone concordato agreements). A range past the expiry is then only flagged
    /// (<c>agreement_expired</c> warning), never blocked.
    /// </summary>
    public bool RemainsInForceUntilReplaced { get; set; }

    /// <summary>Admin's free-text note on the expiry (e.g. "nessun accordo più recente reperito, verificare con...").</summary>
    [MaxLength(500)]
    public string? ExpiryNote { get; set; }

    /// <summary>Last admin edit (fields or verification); null for a row never touched since the seed.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>A-elements the unit must all have, otherwise sub-fascia 1.</summary>
    public int RequiredTypeACount { get; set; } = 2;

    /// <summary>B-elements needed for sub-fascia 2 (MB: 3). Fewer: sub-fascia 1.</summary>
    public int SubFascia2MinTypeBCount { get; set; }

    /// <summary>C-elements needed for sub-fascia 3 (MB: 3).</summary>
    public int SubFascia3MinTypeCCount { get; set; }

    /// <summary>
    /// D-elements among <see cref="SubFascia3QualifyingTypeDElements"/> needed for sub-fascia 3 (MB: 2).
    /// </summary>
    public int SubFascia3MinQualifyingTypeDCount { get; set; }

    /// <summary>D-elements that count for sub-fascia 3, comma separated as in the agreement (MB: D1,D2,D4,D6,D7,D9).</summary>
    [MaxLength(100)]
    public string? SubFascia3QualifyingTypeDElements { get; set; }

    /// <summary>
    /// D-elements (any) with which the agreement allows the maximum of sub-fascia 3 (MB: 4). With fewer, the maximum is
    /// still returned but flagged: the agreement does not say which ceiling applies (class D).
    /// </summary>
    public int SubFascia3MaxMinTypeDCount { get; set; }

    /// <summary>
    /// Heating by stoves in the single rooms puts the unit in sub-fascia 1 unless it has at least this many B-elements
    /// (MB: 4). 0: no stove rule.
    /// </summary>
    public int StoveHeatingMinTypeBCount { get; set; }

    /// <summary>How the percentage coefficients combine (class D in the MB agreement: additive kept, configurable).</summary>
    public CoefficientCombination CoefficientCombination { get; set; } = CoefficientCombination.Additive;

    /// <summary>Maximum uplift of the €/mq values with complete furniture (MB: "fino ad un massimo del 15%").</summary>
    [Precision(5, 2)]
    public decimal FurnishedUpliftPercent { get; set; }

    /// <summary>Maximum uplift of the €/mq values with air conditioning (MB: 5%, on at least half of the surface).</summary>
    [Precision(5, 2)]
    public decimal AirConditioningUpliftPercent { get; set; }

    /// <summary>
    /// Below this surface the square metres may be raised by <see cref="SmallSqmUpliftPercent"/>, up to this surface
    /// (MB: mq × 1,20, at most 40 mq).
    /// </summary>
    public int SmallSqmMax { get; set; }

    [Precision(5, 2)]
    public decimal SmallSqmUpliftPercent { get; set; }

    /// <summary>
    /// Strictly between <see cref="MidSqmMin"/> and <see cref="MidSqmMax"/> the square metres may be raised by
    /// <see cref="MidSqmUpliftPercent"/>, up to <see cref="MidSqmMax"/> (MB: 50 &lt; mq &lt; 60, mq × 1,10, at most 60 mq).
    /// </summary>
    public int MidSqmMin { get; set; }

    public int MidSqmMax { get; set; }

    [Precision(5, 2)]
    public decimal MidSqmUpliftPercent { get; set; }

    /// <summary>
    /// Above this surface the square metres may be reduced by <see cref="LargeSqmReductionPercent"/>, never below this
    /// surface (MB: mq × 0,80, at least 120 mq).
    /// </summary>
    public int LargeSqmMin { get; set; }

    [Precision(5, 2)]
    public decimal LargeSqmReductionPercent { get; set; }

    /// <summary>Share of a garage or covered parking space added to the usable square metres (MB: 50%).</summary>
    [Precision(5, 2)]
    public decimal GarageAppurtenancePercent { get; set; }

    /// <summary>Share of balconies and terraces added to the usable square metres (MB: 30%).</summary>
    [Precision(5, 2)]
    public decimal BalconyAppurtenancePercent { get; set; }

    /// <summary>Share of open parking space, cellar, attic or other appurtenances (MB: 25%).</summary>
    [Precision(5, 2)]
    public decimal OtherAppurtenancePercent { get; set; }

    /// <summary>Share of exclusive green areas (MB: 10%).</summary>
    [Precision(5, 2)]
    public decimal GreenAreaAppurtenancePercent { get; set; }

    /// <summary>Mandatory uplift of the minimum and maximum for a 4-year term (MB: 3%); 5 and 6 years below. No uplift above 6 years.</summary>
    [Precision(5, 2)]
    public decimal Duration4UpliftPercent { get; set; }

    [Precision(5, 2)]
    public decimal Duration5UpliftPercent { get; set; }

    [Precision(5, 2)]
    public decimal Duration6UpliftPercent { get; set; }

    public ICollection<ConcordatoRentBand> Bands { get; set; } = new List<ConcordatoRentBand>();

    public ICollection<TerritorialAgreementSignatory> Signatories { get; set; } = new List<TerritorialAgreementSignatory>();
}
