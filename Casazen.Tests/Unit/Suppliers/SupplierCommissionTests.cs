using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

public class SupplierCommissionTests
{
    [Theory]
    [InlineData(10_000, 10, 1_000)]
    [InlineData(6_000, 10, 600)]
    [InlineData(1_005, 10, 101)]
    [InlineData(1_004, 10, 100)]
    [InlineData(1_006, 10, 101)]
    [InlineData(999, 10, 100)]
    [InlineData(333, 10, 33)]
    [InlineData(1_000, 7.5, 75)]
    [InlineData(1_001, 7.5, 75)]
    [InlineData(1_010, 7.5, 76)]
    [InlineData(3, 50, 2)]
    [InlineData(10_000_000, 50, 5_000_000)]
    [InlineData(10_000_000, 10, 1_000_000)]
    public void FeeCents_IsThePercentageOfTheAmountRoundedAwayFromZero(int amountCents, double percent, int expected)
    {
        Assert.Equal(expected, SupplierCommission.FeeCents(amountCents, (decimal)percent));
    }

    [Theory]
    [InlineData(10_000, 0)]
    [InlineData(10_000, -5)]
    [InlineData(1, 10)]
    [InlineData(4, 10)]
    [InlineData(0, 10)]
    [InlineData(-100, 10)]
    public void FeeCents_ThatRoundsToNothingOrIsNotPositive_IsNotCharged(int amountCents, double percent)
    {
        Assert.Equal(0, SupplierCommission.FeeCents(amountCents, (decimal)percent));
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(2, 100)]
    [InlineData(5, 100)]
    public void FeeCents_ThatWouldNotBeStrictlyBelowTheAmount_IsNotCharged(int amountCents, double percent)
    {
        Assert.Equal(0, SupplierCommission.FeeCents(amountCents, (decimal)percent));
    }

    [Theory]
    [InlineData(6_000, 600, 5_400)]
    [InlineData(6_000, 0, 6_000)]
    [InlineData(1_005, 101, 904)]
    public void NetCents_IsWhatIsLeftAfterTheCommission(int amountCents, int feeCents, int expected)
    {
        Assert.Equal(expected, SupplierCommission.NetCents(amountCents, feeCents));
    }

    [Theory]
    [InlineData(1L, 100L, true)]
    [InlineData(99L, 100L, true)]
    [InlineData(100L, 100L, false)]
    [InlineData(101L, 100L, false)]
    [InlineData(0L, 100L, false)]
    [InlineData(-1L, 100L, false)]
    public void IsSendable_IsTrueOnlyStrictlyBetweenZeroAndTheAmount(long fee, long amount, bool expected)
    {
        Assert.Equal(expected, SupplierCommission.IsSendable(fee, amount));
    }

    [Fact]
    public void FeeCents_NeverReachesTheAmount_ForAnyPercentageUpToTheMaximum()
    {
        for (var amount = 1; amount <= 5_000; amount++)
        {
            foreach (var percent in new[] { 0.01m, 1m, 7.5m, 10m, 33.33m, 50m })
            {
                var fee = SupplierCommission.FeeCents(amount, percent);
                Assert.InRange(fee, 0, amount - 1);
                if (fee > 0)
                    Assert.True(SupplierCommission.IsSendable(fee, amount));
            }
        }
    }
}
