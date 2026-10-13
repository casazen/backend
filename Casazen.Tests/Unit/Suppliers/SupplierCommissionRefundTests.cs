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
        // A refund of more than the payment (a Dashboard refund that was rounded up) is still the whole commission, never more.
        Assert.Equal(600, SupplierCommission.FeeRefundedCents(600, 6_000, 6_500));
    }

    [Theory]
    [InlineData(600, 6_000, 1_500, 150)] // 25 % of the price, 25 % of the commission
    [InlineData(600, 6_000, 3_000, 300)]
    [InlineData(600, 6_000, 5_999, 600)] // 599.9 rounds to the whole commission
    [InlineData(600, 6_000, 1, 0)] // 0.1 of a cent rounds to nothing
    [InlineData(1, 3, 1, 0)] // 0.33 rounds down
    [InlineData(1, 2, 1, 1)] // half a cent rounds up, away from zero
    [InlineData(333, 3_330, 1_000, 100)] // exactly 100
    [InlineData(100, 1_001, 1, 0)] // 0.0999 of a cent
    [InlineData(100, 1_001, 500, 50)] // 49.95 rounds up to 50
    public void FeeRefundedCents_APartialRefund_IsTheCommissionInProportion_RoundedToTheCentAwayFromZero(
        int fee, int amount, int refunded, int expected)
    {
        Assert.Equal(expected, SupplierCommission.FeeRefundedCents(fee, amount, refunded));
    }

    [Theory]
    [InlineData(0, 6_000, 3_000)] // no commission was charged, none goes back
    [InlineData(600, 6_000, 0)] // nothing refunded
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
        // The parts shown per refund are the differences of the cumulative figure, so they always add up to it.
        var first = SupplierCommission.FeeRefundedCents(600, 6_000, 1_234);
        var cumulativeAfterSecond = SupplierCommission.FeeRefundedCents(600, 6_000, 1_234 + 2_345);
        var second = cumulativeAfterSecond - first;

        Assert.Equal(123, first);
        Assert.Equal(358, cumulativeAfterSecond);
        Assert.Equal(235, second);
        Assert.Equal(cumulativeAfterSecond, first + second);
    }

    // ─── The percentage of a supplier's period ───

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EffectivePercent_NoOverride_IsThePlatformPercent()
    {
        Assert.Equal(10m, SupplierCommission.EffectivePercent(10m, null, null, Now));
        // An end date without a percentage means nothing.
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
        // From the end instant on, the period is over: nobody has to remember to take the override off.
        Assert.Equal(10m, SupplierCommission.EffectivePercent(10m, 0m, until, until));
        Assert.Equal(10m, SupplierCommission.EffectivePercent(10m, 0m, until, until.AddDays(1)));
    }
}
