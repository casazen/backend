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
    /// How often the pricing engine runs for this property (e.g. "daily", "hourly").
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string AdaptationFrequency { get; set; } = string.Empty;

    public bool IncludeSeasonality { get; set; } = false;

    public bool IncludePublicHolidays { get; set; } = false;

    /// <summary>
    /// UTC timestamp of the last successful pricing adaptation run.
    /// </summary>
    public DateTime? LastAdaptedAt { get; set; }

    /// <summary>
    /// UTC timestamp of the next scheduled pricing adaptation run.
    /// </summary>
    public DateTime? NextScheduledRunAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
