using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-15a: the commission CasaZen keeps (<see cref="SupplierCommission"/>): a percentage of the price rounded to the cent away
/// from zero, and not charged (not sent to Stripe) when it is not strictly between zero and the amount.
/// </summary>
public class SupplierCommissionTests
{
    [Theory]
    [InlineData(10_000, 10, 1_000)]
    [InlineData(6_000, 10, 600)]
    [InlineData(1_005, 10, 101)] // 100.5 cents: half a cent goes up (away from zero)
    [InlineData(1_004, 10, 100)] // 100.4 cents
    [InlineData(1_006, 10, 101)] // 100.6 cents
    [InlineData(999, 10, 100)] // 99.9 cents
    [InlineData(333, 10, 33)] // 33.3 cents
    [InlineData(1_000, 7.5, 75)]
    [InlineData(1_001, 7.5, 75)] // 75.075 cents
    [InlineData(1_010, 7.5, 76)] // 75.75 cents
    [InlineData(3, 50, 2)] // 1.5 cents: up to 2, which is still below 3
    [InlineData(10_000_000, 50, 5_000_000)]
    [InlineData(10_000_000, 10, 1_000_000)]
    public void FeeCents_IsThePercentageOfTheAmountRoundedAwayFromZero(int amountCents, double percent, int expected)
    {
        Assert.Equal(expected, SupplierCommission.FeeCents(amountCents, (decimal)percent));
    }

    [Theory]
    [InlineData(10_000, 0)] // zero percent: no commission, a free period
    [InlineData(10_000, -5)] // a negative percentage is never a commission
    [InlineData(1, 10)] // 0.1 cent rounds to nothing
    [InlineData(4, 10)] // 0.4 cent rounds to nothing
    [InlineData(0, 10)] // no amount
    [InlineData(-100, 10)]
    public void FeeCents_ThatRoundsToNothingOrIsNotPositive_IsNotCharged(int amountCents, double percent)
    {
        Assert.Equal(0, SupplierCommission.FeeCents(amountCents, (decimal)percent));
    }

    [Theory]
    [InlineData(1, 50)] // 0.5 cent rounds up to 1, which is the whole amount: Stripe wants it strictly below
    [InlineData(2, 100)] // a commission of the whole price is never charged
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
    [InlineData(100L, 100L, false)] // not below the amount
    [InlineData(101L, 100L, false)]
    [InlineData(0L, 100L, false)] // never an explicit zero (A3-40)
    [InlineData(-1L, 100L, false)]
    public void IsSendable_IsTrueOnlyStrictlyBetweenZeroAndTheAmount(long fee, long amount, bool expected)
    {
        Assert.Equal(expected, SupplierCommission.IsSendable(fee, amount));
    }

    [Fact]
    public void FeeCents_NeverReachesTheAmount_ForAnyPercentageUpToTheMaximum()
    {
        // The configuration allows up to 50 %: with it, the commission stays below the amount for every amount the catalog allows
        // and for the smallest ones (where rounding up could reach it, the commission is dropped, never sent equal to the amount).
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
