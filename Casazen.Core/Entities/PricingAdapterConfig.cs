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

    /// <summary>Tenant of the row, copied from <see cref="Property"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// How often the suggestions are recomputed: <see cref="SeasonalSuggestionSchedule.Daily"/> or
    /// <see cref="SeasonalSuggestionSchedule.Weekly"/>, compared by Rome calendar dates (<see cref="SeasonalSuggestionSchedule"/>).
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string AdaptationFrequency { get; set; } = string.Empty;

    /// <summary>Apply the high/low season months.</summary>
    public bool IncludeSeasonality { get; set; } = false;

    /// <summary>Apply <see cref="HolidayMultiplier"/> on Italian national holidays (<see cref="ItalianPublicHolidays"/>).</summary>
    public bool IncludePublicHolidays { get; set; } = false;

    /// <summary>High-season months (1-12). Starts from the example rule (<see cref="SeasonalPricingRules.Example"/>).</summary>
    public List<int> HighSeasonMonths { get; set; } = [.. SeasonalPricingRules.ExampleHighSeasonMonths];

    /// <summary>Multiplier of the nightly rate in the high-season months.</summary>
    [Precision(4, 2)]
    public decimal HighSeasonMultiplier { get; set; } = SeasonalPricingRules.ExampleHighSeasonMultiplier;

    /// <summary>Low-season months (1-12), never also a high-season month.</summary>
    public List<int> LowSeasonMonths { get; set; } = [.. SeasonalPricingRules.ExampleLowSeasonMonths];

    /// <summary>Multiplier of the nightly rate in the low-season months.</summary>
    [Precision(4, 2)]
    public decimal LowSeasonMultiplier { get; set; } = SeasonalPricingRules.ExampleLowSeasonMultiplier;

    /// <summary>Multiplier of the nightly rate on national holidays (takes precedence over the season).</summary>
    [Precision(4, 2)]
    public decimal HolidayMultiplier { get; set; } = SeasonalPricingRules.ExampleHolidayMultiplier;

    /// <summary>
    /// UTC instant of the last computation of the suggestions; <c>null</c> until the first one. The next one is due from
    /// the Rome date <see cref="SeasonalSuggestionSchedule.NextRunOn"/>.
    /// </summary>
    public DateTime? LastAdaptedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
