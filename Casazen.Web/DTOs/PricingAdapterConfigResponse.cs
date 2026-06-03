namespace Casazen.Web.DTOs;

/// <summary>
/// Current AI pricing adapter configuration for a property.
/// </summary>
public class PricingAdapterConfigResponse
{
    /// <summary>The property this configuration belongs to.</summary>
    public Guid PropertyId { get; set; }

    /// <summary>Whether AI-driven dynamic pricing is active.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>UTC timestamp of the next scheduled pricing run, or <c>null</c> if not scheduled.</summary>
    public DateTime? NextScheduledRunAt { get; set; }

    /// <summary>Pricing recalculation frequency: <c>daily</c> or <c>weekly</c>.</summary>
    public string AdaptationFrequency { get; set; } = string.Empty;

    /// <summary>Whether seasonal demand multipliers are applied.</summary>
    public bool IncludeSeasonality { get; set; }

    /// <summary>Whether Italian public-holiday surge multipliers are applied.</summary>
    public bool IncludePublicHolidays { get; set; }

    /// <summary>UTC timestamp of the last successful pricing adaptation, or <c>null</c> if never run.</summary>
    public DateTime? LastAdaptedAt { get; set; }

    /// <summary>UTC timestamp when this configuration was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent update to this configuration.</summary>
    public DateTime UpdatedAt { get; set; }
}
