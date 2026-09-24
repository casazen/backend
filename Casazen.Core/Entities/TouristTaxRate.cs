using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Tourist tax rate of a comune: the single source of the tourist tax (task BK-03; the old <c>TaxRates</c> table is
/// gone). A comune can have several rows: one per accommodation category, per season or per period of validity.
/// The amount is computed only by <see cref="Casazen.Core.TouristTax.TouristTaxCalculator"/>.
/// </summary>
[Table("TouristTaxRates")]
public class TouristTaxRate
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// City name (e.g., "Roma", "Milano", "Firenze")
    /// </summary>
    [Required, MaxLength(100)]
    public string City { get; set; } = string.Empty;

    /// <summary>
    /// ISTAT code of the comune (6 digits), when known. A rate and a comune that both carry an ISTAT code are matched
    /// by code only; otherwise by the normalized name (<see cref="Casazen.Core.TouristTax.TouristTaxComune"/>).
    /// </summary>
    [MaxLength(6)]
    public string? IstatCode { get; set; }

    /// <summary>
    /// Region code (e.g., "LAZ", "LOM", "TOS")
    /// </summary>
    [MaxLength(10)]
    public string RegionCode { get; set; } = string.Empty;

    /// <summary>
    /// Accommodation category the rate applies to, as named by the comune (e.g. Roma "Case e appartamenti per vacanze,
    /// categoria 1", Venezia "Gruppo 1 (A/1, A/8, A/9)"). Null: every accommodation of the comune.
    /// </summary>
    [MaxLength(100)]
    public string? AccommodationCategory { get; set; }

    /// <summary>
    /// First day of the season the rate applies to, <c>MM-dd</c>, repeated every year (Venezia high season
    /// <c>02-01</c>..<c>12-31</c>). Null together with <see cref="SeasonEnd"/>: the whole year.
    /// </summary>
    [MaxLength(5)]
    public string? SeasonStart { get; set; }

    /// <summary>Last day (inclusive) of the season, <c>MM-dd</c>. May be before <see cref="SeasonStart"/> (winter seasons).</summary>
    [MaxLength(5)]
    public string? SeasonEnd { get; set; }

    /// <summary>Fixed amount per person per night, or a percentage of the night price.</summary>
    public TouristTaxCalculationMethod CalculationMethod { get; set; } = TouristTaxCalculationMethod.PerPersonPerNight;

    /// <summary>
    /// Tax amount per person per night in euros (method <see cref="TouristTaxCalculationMethod.PerPersonPerNight"/>;
    /// 0 for a percentage rate).
    /// </summary>
    [Precision(18, 2)]
    public decimal RatePerPersonPerNight { get; set; }

    /// <summary>Percentage of the night price per person (method <see cref="TouristTaxCalculationMethod.PercentOfNightlyPrice"/>), e.g. 10.5.</summary>
    [Precision(5, 2)]
    public decimal? PercentOfNightlyPrice { get; set; }

    /// <summary>Maximum amount per person per night of a percentage rate (e.g. 7.00). Null: no cap.</summary>
    [Precision(18, 2)]
    public decimal? CapPerPersonPerNight { get; set; }

    /// <summary>
    /// Maximum nights of a stay subject to the tax: only the first <c>MaxNights</c> nights are taxed (e.g. 5-14).
    /// </summary>
    public int? MaxNights { get; set; }

    /// <summary>
    /// Guests younger than this (age at check-in) are exempt. Adults (18 and over) are never exempt by age, so the
    /// value is between 0 (no age exemption) and 18 (every minor exempt).
    /// </summary>
    public int MinimumAge { get; set; } = 14;

    /// <summary>
    /// Reduced band: guests from <see cref="MinimumAge"/> up to this age included pay
    /// <see cref="ReducedRatePerPersonPerNight"/> (Venezia: reduction "per i minori da 10 a 16 anni"). Null: no band.
    /// Fixed rates only.
    /// </summary>
    public int? ReducedRateMaxAge { get; set; }

    /// <summary>
    /// Amount per person per night of the reduced band, as published by the comune (Venezia publishes 1,70 for a
    /// 3,50 rate "ridotta 50%": the amount is data, never computed).
    /// </summary>
    [Precision(18, 2)]
    public decimal? ReducedRatePerPersonPerNight { get; set; }

    /// <summary>
    /// Whether this rate is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Date when this rate becomes effective
    /// </summary>
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date when this rate expires (null if no expiration)
    /// </summary>
    public DateTime? EffectiveTo { get; set; }

    [MaxLength(500)]
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Source of the rate: URL of the page or act of the comune it was read from. Null when nobody recorded it.
    /// </summary>
    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    /// <summary>
    /// How the amount was checked against <see cref="SourceUrl"/> (U / D / T). Null when not stated.
    /// </summary>
    public TouristTaxRateVerification? VerificationLevel { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
