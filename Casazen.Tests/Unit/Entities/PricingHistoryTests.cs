using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Entities;

public class PricingHistoryTests
{
    [Fact]
    public void CreatePricingHistory_WithValidData_InitializesCorrectly()
    {
        var propertyId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var history = new PricingHistory
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            AdaptationDate = now,
            PreviousPrice = 100m,
            NewPrice = 120m,
            ChangeReason = "High demand period",
            AiConfidence = 0.95m,
            OtasSynced = "[\"Airbnb\", \"Booking\"]",
            SyncStatus = "Synced",
            CreatedAt = now
        };

        Assert.NotEqual(Guid.Empty, history.Id);
        Assert.Equal(propertyId, history.PropertyId);
        Assert.Equal(now, history.AdaptationDate);
        Assert.Equal(100m, history.PreviousPrice);
        Assert.Equal(120m, history.NewPrice);
        Assert.Equal("High demand period", history.ChangeReason);
        Assert.Equal(0.95m, history.AiConfidence);
        Assert.Equal("[\"Airbnb\", \"Booking\"]", history.OtasSynced);
        Assert.Equal("Synced", history.SyncStatus);
        Assert.Equal(now, history.CreatedAt);
    }

    [Fact]
    public void CreatePricingHistory_WithDefaults_InitializesWithCorrectDefaults()
    {
        var history = new PricingHistory
        {
            PropertyId = Guid.NewGuid(),
            PreviousPrice = 80m,
            NewPrice = 100m,
            ChangeReason = "Adjustment",
            AiConfidence = 0.85m,
            SyncStatus = "Pending"
        };

        Assert.NotEqual(default, history.AdaptationDate);
        Assert.Equal(string.Empty, history.OtasSynced);
        Assert.NotEqual(default, history.CreatedAt);
    }

    [Fact]
    public void CreatePricingHistory_WithValidConfidenceScores_StoresCorrectly()
    {
        var scores = new[] { 0.0m, 0.5m, 0.75m, 1.0m };

        foreach (var score in scores)
        {
            var history = new PricingHistory
            {
                PropertyId = Guid.NewGuid(),
                PreviousPrice = 100m,
                NewPrice = 110m,
                ChangeReason = "Test",
                AiConfidence = score,
                SyncStatus = "Pending"
            };
            Assert.Equal(score, history.AiConfidence);
        }
    }

    [Fact]
    public void CreatePricingHistory_WithDifferentPrices_CalculatesCorrectly()
    {
        var testCases = new (decimal, decimal, decimal)[]
        {
            (100m, 120m, 20m),
            (100m, 80m, -20m),
            (100m, 100m, 0m)
        };

        foreach (var (previous, newPrice, expectedChange) in testCases)
        {
            var history = new PricingHistory
            {
                PropertyId = Guid.NewGuid(),
                PreviousPrice = previous,
                NewPrice = newPrice,
                ChangeReason = "Test",
                AiConfidence = 0.85m,
                SyncStatus = "Pending"
            };
            var actualChange = history.NewPrice - history.PreviousPrice;
            Assert.Equal(expectedChange, actualChange);
        }
    }

    [Fact]
    public void CreatePricingHistory_WithDifferentSyncStatuses_StoresCorrectly()
    {
        var statuses = new[] { "Pending", "Synced", "Failed", "PartialSync" };

        foreach (var status in statuses)
        {
            var history = new PricingHistory
            {
                PropertyId = Guid.NewGuid(),
                PreviousPrice = 100m,
                NewPrice = 110m,
                ChangeReason = "Test",
                AiConfidence = 0.85m,
                SyncStatus = status
            };
            Assert.Equal(status, history.SyncStatus);
        }
    }

    [Fact]
    public void CreatePricingHistory_WithJsonOtasList_StoresCorrectly()
    {
        var otasList = "[\"Airbnb\", \"Booking\", \"Expedia\", \"Vrbo\"]";
        var emptyOtasList = "[]";

        var historyWithOtas = new PricingHistory
        {
            PropertyId = Guid.NewGuid(),
            PreviousPrice = 100m,
            NewPrice = 110m,
            ChangeReason = "Test",
            AiConfidence = 0.85m,
            OtasSynced = otasList,
            SyncStatus = "Synced"
        };

        var historyWithoutOtas = new PricingHistory
        {
            PropertyId = Guid.NewGuid(),
            PreviousPrice = 100m,
            NewPrice = 110m,
            ChangeReason = "Test",
            AiConfidence = 0.85m,
            OtasSynced = emptyOtasList,
            SyncStatus = "Pending"
        };

        Assert.Equal(otasList, historyWithOtas.OtasSynced);
        Assert.Equal(emptyOtasList, historyWithoutOtas.OtasSynced);
    }
}
