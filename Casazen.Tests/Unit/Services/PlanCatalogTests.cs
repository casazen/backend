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
