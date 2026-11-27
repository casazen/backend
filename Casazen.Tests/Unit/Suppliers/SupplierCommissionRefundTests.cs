using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-15b: a refund sent with <c>refund_application_fee=true</c> gives CasaZen's commission back in full for a refund in full and in
/// proportion for a partial one (a partial refund reduces the commission in proportion), and a supplier's own percentage applies
/// only while its period lasts. Pure functions: the percentage always comes in from the caller.
/// </summary>
public class SupplierCommissionRefundTests
{
    [Fact]
    public void FeeRefundedCents_ARefundInFull_GivesBackTheWholeCommission()
    {
        Assert.Equal(600, SupplierCommission.FeeRefundedCents(feeCents: 600, amountCents: 6_000, refundedCents: 6_000));
        Assert.Equal(600, SupplierCommission.FeeRefundedCents(600, 6_000, 6_500));
    }

    [Theory]
    [InlineData(600, 6_000, 1_500, 150)]
    [InlineData(600, 6_000, 3_000, 300)]
    [InlineData(600, 6_000, 5_999, 600)]
    [InlineData(600, 6_000, 1, 0)]
    [InlineData(1, 3, 1, 0)]
    [InlineData(1, 2, 1, 1)]
    [InlineData(333, 3_330, 1_000, 100)]
    [InlineData(100, 1_001, 1, 0)]
    [InlineData(100, 1_001, 500, 50)]
    public void FeeRefundedCents_APartialRefund_IsTheCommissionInProportion_RoundedToTheCentAwayFromZero(
        int fee, int amount, int refunded, int expected)
    {
        Assert.Equal(expected, SupplierCommission.FeeRefundedCents(fee, amount, refunded));
    }

    [Theory]
    [InlineData(0, 6_000, 3_000)]
    [InlineData(600, 6_000, 0)]
    [InlineData(600, 6_000, -5)]
    [InlineData(600, 0, 100)]
    [InlineData(-1, 6_000, 3_000)]
    public void FeeRefundedCents_NothingToGiveBack_IsZero(int fee, int amount, int refunded)
    {
        Assert.Equal(0, SupplierCommission.FeeRefundedCents(fee, amount, refunded));
    }

    [Fact]
    public void FeeRefundedCents_IsNeverMoreThanTheCommission_NorDecreasesAsTheRefundGrows()
    {
        const int fee = 987;
        const int amount = 9_870;
        var previous = 0;
        for (var refunded = 0; refunded <= amount + 100; refunded += 7)
        {
            var back = SupplierCommission.FeeRefundedCents(fee, amount, refunded);
            Assert.InRange(back, previous, fee);
            previous = back;
        }

        Assert.Equal(fee, previous);
    }

    [Fact]
    public void FeeRefundedCents_TwoPartialRefunds_AddUpToTheProportionOfTheTotal()
    {
        var first = SupplierCommission.FeeRefundedCents(600, 6_000, 1_234);
        var cumulativeAfterSecond = SupplierCommission.FeeRefundedCents(600, 6_000, 1_234 + 2_345);
        var second = cumulativeAfterSecond - first;

        Assert.Equal(123, first);
        Assert.Equal(358, cumulativeAfterSecond);
        Assert.Equal(235, second);
        Assert.Equal(cumulativeAfterSecond, first + second);
    }

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EffectivePercent_NoOverride_IsThePlatformPercent()
    {
        Assert.Equal(10m, SupplierCommission.EffectivePercent(10m, null, null, Now));
        Assert.Equal(10m, SupplierCommission.EffectivePercent(10m, null, Now.AddDays(10), Now));
    }

    [Fact]
    public void EffectivePercent_AnOverrideWithNoEnd_StaysInForce_AFreePeriodIsZero()
    {
        Assert.Equal(0m, SupplierCommission.EffectivePercent(10m, 0m, null, Now));
        Assert.Equal(7.5m, SupplierCommission.EffectivePercent(10m, 7.5m, null, Now.AddYears(5)));
    }

    [Fact]
    public void EffectivePercent_AnOverrideThatHasNotEnded_Applies_AndAfterItsEndThePlatformPercentIsBack()
    {
        var until = Now.AddDays(30);

        Assert.Equal(0m, SupplierCommission.EffectivePercent(10m, 0m, until, Now));
        Assert.Equal(0m, SupplierCommission.EffectivePercent(10m, 0m, until, until.AddTicks(-1)));
        Assert.Equal(10m, SupplierCommission.EffectivePercent(10m, 0m, until, until));
        Assert.Equal(10m, SupplierCommission.EffectivePercent(10m, 0m, until, until.AddDays(1)));
    }
}
