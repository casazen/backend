using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IPricingAdapterService
{
    /// <summary>
    /// Calculate the pricing multiplier for a specific date based on seasonality and public holidays.
    /// Multiplier > 1.0 indicates higher prices (high season or holidays),
    /// Multiplier < 1.0 indicates lower prices (low season).
    /// </summary>
    /// <param name="date">The date to calculate the multiplier for (UTC).</param>
    /// <param name="includeSeasonality">Whether to apply seasonal adjustments.</param>
    /// <param name="includePublicHolidays">Whether to apply public holiday adjustments.</param>
    /// <returns>A decimal multiplier to apply to the base price.</returns>
    Task<decimal> CalculatePricingMultiplierAsync(DateTime date, bool includeSeasonality, bool includePublicHolidays);

    /// <summary>
    /// Get the pricing adapter configuration for a property.
    /// </summary>
    Task<PricingAdapterConfig?> GetConfigAsync(Guid propertyId);

    /// <summary>
    /// Save or update a pricing adapter configuration.
    /// </summary>
    Task<PricingAdapterConfig> SaveConfigAsync(PricingAdapterConfig config);

    /// <summary>
    /// Record a pricing history entry for a property.
    /// </summary>
    Task<PricingHistory> RecordPricingChangeAsync(
        Guid propertyId,
        decimal previousPrice,
        decimal newPrice,
        string changeReason,
        decimal aiConfidence,
        string? otasSynced = null,
        string? syncStatus = null);
}
