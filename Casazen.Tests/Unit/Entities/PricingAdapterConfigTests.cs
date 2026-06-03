using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Entities;

public class PricingAdapterConfigTests
{
    [Fact]
    public void CreatePricingAdapterConfig_WithValidData_InitializesCorrectly()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Act
        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            AdaptationFrequency = "daily",
            IncludeSeasonality = true,
            IncludePublicHolidays = true,
            LastAdaptedAt = now.AddHours(-1),
            NextScheduledRunAt = now.AddHours(1),
            CreatedAt = now,
            UpdatedAt = now
        };

        // Assert
        Assert.NotEqual(Guid.Empty, config.Id);
        Assert.Equal(propertyId, config.PropertyId);
        Assert.True(config.IsEnabled);
        Assert.Equal("daily", config.AdaptationFrequency);
        Assert.True(config.IncludeSeasonality);
        Assert.True(config.IncludePublicHolidays);
        Assert.NotNull(config.LastAdaptedAt);
        Assert.NotNull(config.NextScheduledRunAt);
    }

    [Fact]
    public void CreatePricingAdapterConfig_WithDefaults_InitializesWithCorrectDefaults()
    {
        // Arrange & Act
        var config = new PricingAdapterConfig
        {
            PropertyId = Guid.NewGuid(),
            AdaptationFrequency = "hourly"
        };

        // Assert
        Assert.False(config.IsEnabled);
        Assert.False(config.IncludeSeasonality);
        Assert.False(config.IncludePublicHolidays);
        Assert.Null(config.LastAdaptedAt);
        Assert.Null(config.NextScheduledRunAt);
        Assert.NotEqual(default, config.CreatedAt);
        Assert.NotEqual(default, config.UpdatedAt);
    }

    [Fact]
    public void CreatePricingAdapterConfig_WithDifferentFrequencies_StoresCorrectly()
    {
        // Arrange
        var frequencies = new[] { "hourly", "daily", "weekly", "monthly" };

        // Act & Assert
        foreach (var frequency in frequencies)
        {
            var config = new PricingAdapterConfig
            {
                PropertyId = Guid.NewGuid(),
                AdaptationFrequency = frequency
            };
            Assert.Equal(frequency, config.AdaptationFrequency);
        }
    }

    [Fact]
    public void CreatePricingAdapterConfig_AllCombinationsOfFlags_StoreCorrectly()
    {
        // Arrange
        var combinations = new[]
        {
            (seasonality: true, holidays: true),
            (seasonality: true, holidays: false),
            (seasonality: false, holidays: true),
            (seasonality: false, holidays: false)
        };

        // Act & Assert
        foreach (var (seasonality, holidays) in combinations)
        {
            var config = new PricingAdapterConfig
            {
                PropertyId = Guid.NewGuid(),
                AdaptationFrequency = "daily",
                IncludeSeasonality = seasonality,
                IncludePublicHolidays = holidays
            };
            Assert.Equal(seasonality, config.IncludeSeasonality);
            Assert.Equal(holidays, config.IncludePublicHolidays);
        }
    }
}
