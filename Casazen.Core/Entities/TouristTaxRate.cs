using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("TouristTaxRates")]
public class TouristTaxRate
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(100)]
    public string City { get; set; } = string.Empty;

    /// <summary>
    /// ISTAT code of the comune (6 digits), when known. A rate and a comune that both carry an ISTAT code are matched
    /// by code only; otherwise by the normalized name (<see cref="Casazen.Core.TouristTax.TouristTaxComune"/>).
    /// </summary>
    [MaxLength(6)]
    public string? IstatCode { get; set; }

    [MaxLength(10)]
    public string RegionCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? AccommodationCategory { get; set; }

    [MaxLength(5)]
    public string? SeasonStart { get; set; }

    [MaxLength(5)]
    public string? SeasonEnd { get; set; }

    public TouristTaxCalculationMethod CalculationMethod { get; set; } = TouristTaxCalculationMethod.PerPersonPerNight;

    [Precision(18, 2)]
    public decimal RatePerPersonPerNight { get; set; }

    [Precision(5, 2)]
    public decimal? PercentOfNightlyPrice { get; set; }

    [Precision(18, 2)]
    public decimal? CapPerPersonPerNight { get; set; }

    public int? MaxNights { get; set; }

    public int MinimumAge { get; set; } = 14;

    public int? ReducedRateMaxAge { get; set; }

    [Precision(18, 2)]
    public decimal? ReducedRatePerPersonPerNight { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;

    public DateTime? EffectiveTo { get; set; }

    [MaxLength(500)]
    public string Notes { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    [MaxLength(200)]
    public string? SourceAuthority { get; set; }

    public DateTime? SourceRetrievedAt { get; set; }

    public TouristTaxRateVerification? VerificationLevel { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
