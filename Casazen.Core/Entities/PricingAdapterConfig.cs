using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Per-property configuration for the AI-driven dynamic pricing engine.
/// Controls whether and how often the pricing adapter runs for a given property.
/// Part of Epic: AI-Driven Dynamic Pricing Engine.
/// </summary>
[Table("PricingAdapterConfigs")]
public class PricingAdapterConfig
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// UTC timestamp of the last successful pricing adaptation run.
    /// </summary>
    public DateTime? LastAdaptedAt { get; set; }

    /// <summary>
    /// UTC timestamp of the next scheduled pricing adaptation run.
    /// </summary>
    public DateTime? NextScheduledRunAt { get; set; }

    /// <summary>
    /// How often the pricing engine runs for this property (e.g. "daily", "hourly").
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string AdaptationFrequency { get; set; } = string.Empty;

    /// <summary>
    /// Whether to include seasonal demand patterns in pricing calculations.
    /// </summary>
    public bool IncludeSeasonality { get; set; } = false;

    /// <summary>
    /// Whether to include public holidays in pricing calculations.
    /// </summary>
    public bool IncludePublicHolidays { get; set; } = false;

    /// <summary>
    /// UTC timestamp of when this configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp of the last update to this configuration.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
