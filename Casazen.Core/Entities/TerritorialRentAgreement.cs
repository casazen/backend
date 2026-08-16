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

    public int RequiredTypeACount { get; set; } = 2;

    [Precision(5, 2)]
    public decimal FurnishedUpliftPercent { get; set; }

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
    public decimal Duration4UpliftPercent { get; set; }

    [Precision(5, 2)]
    public decimal Duration5UpliftPercent { get; set; }

    [Precision(5, 2)]
    public decimal Duration6UpliftPercent { get; set; }

    public ICollection<ConcordatoRentBand> Bands { get; set; } = new List<ConcordatoRentBand>();

    public ICollection<TerritorialAgreementSignatory> Signatories { get; set; } = new List<TerritorialAgreementSignatory>();
}
