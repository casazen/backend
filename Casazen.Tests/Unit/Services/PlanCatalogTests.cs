using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PlanCatalogTests
{
    [Theory]
    [InlineData("Starter", PlanTier.Starter)]
    [InlineData("pro", PlanTier.Pro)]
    [InlineData(" Scale ", PlanTier.Scale)]
    public void TryParseTier_TierName_ReturnsTrueWithTier(string value, PlanTier expected)
    {
        var success = PlanCatalog.TryParseTier(value, out var tier);

        Assert.True(success);
        Assert.Equal(expected, tier);
    }

    /// <summary>Decision D13: Starter 2 people, Pro 10, Scale unlimited.</summary>
    [Theory]
    [InlineData(PlanTier.Starter, 2)]
    [InlineData(PlanTier.Pro, 10)]
    [InlineData(PlanTier.Scale, int.MaxValue)]
    public void MaxSeatsFor_Tier_IsTheDecidedNumberOfPeople(PlanTier tier, int expected)
    {
        Assert.Equal(expected, PlanCatalog.MaxSeatsFor(tier));
        Assert.Equal(expected, PlanCatalog.All.Single(e => e.Tier == tier).MaxSeats);
    }

    [Fact]
    public void All_EveryTier_HasPositiveSeatsGrowingWithTheRank()
    {
        var seats = PlanCatalog.All.OrderBy(e => PlanCatalog.Rank(e.Tier)).Select(e => e.MaxSeats).ToList();

        Assert.All(seats, s => Assert.True(s > 0));
        Assert.Equal(seats.Order().ToList(), seats);
    }

    /// <summary>PL-07 (A1-35): only tier names, never their numbers or a comma-separated combination.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("9")]
    [InlineData("Starter,Pro")]
    [InlineData("Enterprise")]
    public void TryParseTier_NotATierName_ReturnsFalse(string? value)
    {
        var success = PlanCatalog.TryParseTier(value, out _);

        Assert.False(success);
    }
}
