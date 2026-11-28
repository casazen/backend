using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Casazen.Core.Pricing;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Per-property configuration of the seasonal price suggestions ("Suggerimenti stagionali", D4, PC-15): whether they are
/// computed, how often, and the host's explicit rules (months or national holidays with a multiplier of the property's
/// nightly rate). Suggestions are proposals only: they never change the price of a quote or a booking.
/// </summary>
[Table("PricingAdapterConfigs")]
public class PricingAdapterConfig : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public Guid OrgId { get; set; }

    public bool IsEnabled { get; set; } = false;

    [Required]
    [MaxLength(50)]
    public string AdaptationFrequency { get; set; } = string.Empty;

    public bool IncludeSeasonality { get; set; } = false;

    public bool IncludePublicHolidays { get; set; } = false;

    public List<int> HighSeasonMonths { get; set; } = [.. SeasonalPricingRules.ExampleHighSeasonMonths];

    [Precision(4, 2)]
    public decimal HighSeasonMultiplier { get; set; } = SeasonalPricingRules.ExampleHighSeasonMultiplier;

    public List<int> LowSeasonMonths { get; set; } = [.. SeasonalPricingRules.ExampleLowSeasonMonths];

    [Precision(4, 2)]
    public decimal LowSeasonMultiplier { get; set; } = SeasonalPricingRules.ExampleLowSeasonMultiplier;

    [Precision(4, 2)]
    public decimal HolidayMultiplier { get; set; } = SeasonalPricingRules.ExampleHolidayMultiplier;

    public DateTime? LastAdaptedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
