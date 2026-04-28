using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Tourist tax rates by city/region per Italian regulations.
/// Tax rates vary significantly by location.
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
    /// Region code (e.g., "LAZ", "LOM", "TOS")
    /// </summary>
    [MaxLength(10)]
    public string RegionCode { get; set; } = string.Empty;

    /// <summary>
    /// Tax amount per person per night in euros
    /// </summary>
    [Precision(18, 2)]
    public decimal RatePerPersonPerNight { get; set; }

    /// <summary>
    /// Maximum nights subject to tourist tax (e.g., some cities cap at 5-10 nights)
    /// </summary>
    public int? MaxNights { get; set; }

    /// <summary>
    /// Minimum age for tax (children usually exempt, e.g., under 14)
    /// </summary>
    public int MinimumAge { get; set; } = 14;

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
