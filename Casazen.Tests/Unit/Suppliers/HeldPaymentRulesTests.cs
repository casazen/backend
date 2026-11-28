using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-15c-a (decision VT-D1): the money rules of the payment CasaZen holds, one test per rule where a mistake costs money: the amount and the
/// commission, the moment the supplier is paid, one transfer per payment, a refund and a release that never share the same euros, the block of
/// the card that expires, and the delay between two tries of a transfer. <see cref="HeldPaymentRules"/> is pure, so every case is a table row.
/// </summary>
public class HeldPaymentRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static HeldReleaseFacts Facts(
        ServicePaymentStatus status = ServicePaymentStatus.Held,
        DateTime? releaseAt = null,
        DateTime? nextAttemptAt = null,
        string? transferId = null,
        int net = 9_000,
        int refunded = 0,
        int inProgress = 0,
        ServiceRequestStatus request = ServiceRequestStatus.Completato,
        bool confirmed = false) =>
        new(status, releaseAt ?? Now.AddMinutes(-1), nextAttemptAt, transferId, net, refunded, inProgress, request, confirmed);

    [Theory]
    [InlineData(10_000, 10, 1_000, 9_000)]
    [InlineData(10_000, 0, 0, 10_000)]
    [InlineData(333, 10, 33, 300)]
    [InlineData(335, 10, 34, 301)]
    [InlineData(5, 10, 1, 4)]
    [InlineData(1, 10, 0, 1)]
    public void TheSupplierGetsTheAmountMinusTheCommissionCasaZenKeeps(int amount, int percent, int expectedFee, int expectedNet)
    {
        var fee = SupplierCommission.FeeCents(amount, percent);
        var net = SupplierCommission.NetCents(amount, fee);

        Assert.Equal(expectedFee, fee);
        Assert.Equal(expectedNet, net);
        Assert.Equal(amount, fee + net);
        Assert.Equal(net, HeldPaymentRules.ReleasableCents(net, 0, 0));
    }

    [Fact]
    public void ARefundAndAReleaseNeverShareTheSameEuros_ForEveryAmountAndRefund()
    {
        // The invariant of the whole cycle: what went back to the payer plus what is left for the supplier never exceeds what the payer paid,
        // and the commission is only given up when everything goes back.
        foreach (var amount in new[] { 1, 2, 3, 99, 100, 101, 333, 6_000, 99_999 })
        {
            foreach (var percent in new[] { 0m, 5m, 10m, 12.5m, 50m })
            {
                var fee = SupplierCommission.FeeCents(amount, percent);
                var net = SupplierCommission.NetCents(amount, fee);
                for (var refunded = 0; refunded <= amount; refunded += Math.Max(1, amount / 17))
                {
                    var releasable = HeldPaymentRules.ReleasableCents(net, refunded, 0);
                    var refundable = HeldPaymentRules.RefundableCents(amount, refunded, 0);

                    Assert.InRange(releasable, 0, net);
                    Assert.True(refunded + releasable <= amount, $"{amount}/{percent}%/{refunded}: {refunded} + {releasable} > {amount}");
                    Assert.Equal(amount - refunded, refundable);
                }
            }
        }
    }

    [Theory]
    [InlineData(9_000, 0, 0, 9_000)]
    [InlineData(9_000, 2_000, 0, 7_000)]
    [InlineData(9_000, 2_000, 3_000, 4_000)]
    [InlineData(9_000, 9_000, 0, 0)]
    [InlineData(9_000, 10_000, 0, 0)]
    public void ReleasableCents_IsTheNetMinusWhatWasGivenBackAndWhatIsBeingGivenBack(int net, int refunded, int inProgress, int expected) =>
        Assert.Equal(expected, HeldPaymentRules.ReleasableCents(net, refunded, inProgress));

    [Theory]
    [InlineData(10_000, 0, 0, 10_000)]
    [InlineData(10_000, 4_000, 1_000, 5_000)]
    [InlineData(10_000, 10_000, 0, 0)]
    [InlineData(10_000, 12_000, 0, 0)]
    public void RefundableCents_IsTheAmountMinusWhatWasGivenBackAndWhatIsBeingGivenBack(int amount, int refunded, int inProgress, int expected) =>
        Assert.Equal(expected, HeldPaymentRules.RefundableCents(amount, refunded, inProgress));

    [Fact]
    public void ReleaseAt_IsTheLaterOfTheCompletionAndTheCharge_PlusTheWindow()
    {
        var charge = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromMinutes(60);

        Assert.Equal(charge.AddHours(5).AddMinutes(60), HeldPaymentRules.ReleaseAt(charge.AddHours(5), charge, window));

        Assert.Equal(charge.AddMinutes(60), HeldPaymentRules.ReleaseAt(charge.AddMinutes(-30), charge, window));

        Assert.Equal(charge, HeldPaymentRules.ReleaseAt(charge, charge, TimeSpan.Zero));
    }

    [Fact]
    public void Release_WhenTheTimeHasComeAndNothingStandsInTheWay_Releases() =>
        Assert.Equal(HeldReleaseVerdict.Release, HeldPaymentRules.Release(Facts(), Now));

    [Fact]
    public void Release_APaymentThatHasATransferIsNeverReleasedAgain()
    {
        Assert.Equal(HeldReleaseVerdict.AlreadyReleased, HeldPaymentRules.Release(Facts(transferId: "tr_1"), Now));
        Assert.Equal(HeldReleaseVerdict.AlreadyReleased, HeldPaymentRules.Release(Facts(status: ServicePaymentStatus.Released, transferId: "tr_1"), Now));

        Assert.Equal(HeldReleaseVerdict.AlreadyReleased, HeldPaymentRules.Release(Facts(transferId: "tr_1", confirmed: true), Now));
        Assert.Equal(
            HeldReleaseVerdict.AlreadyReleased,
            HeldPaymentRules.Release(Facts(status: ServicePaymentStatus.TransferFailed, transferId: "tr_1"), Now));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested)]
    [InlineData(ServicePaymentStatus.Processing)]
    [InlineData(ServicePaymentStatus.Paid)]
    [InlineData(ServicePaymentStatus.Failed)]
    [InlineData(ServicePaymentStatus.Canceled)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded)]
    [InlineData(ServicePaymentStatus.Refunded)]
    [InlineData(ServicePaymentStatus.NeedsReview)]
    [InlineData(ServicePaymentStatus.Authorized)]
    [InlineData(ServicePaymentStatus.Disputed)]
    public void Release_OnlyHeldMoneyIsReleased(ServicePaymentStatus status) =>
        Assert.Equal(HeldReleaseVerdict.NotReleasable, HeldPaymentRules.Release(Facts(status: status, confirmed: true), Now));

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public void Release_OnlyACompletedRequestIsPaid_ACancelledOneNever(ServiceRequestStatus request) =>
        Assert.Equal(HeldReleaseVerdict.NotReleasable, HeldPaymentRules.Release(Facts(request: request, confirmed: true), Now));

    [Fact]
    public void Release_TransferFailedIsTriedAgain_WhenItsDelayHasPassed()
    {
        var failed = Facts(status: ServicePaymentStatus.TransferFailed, nextAttemptAt: Now.AddMinutes(-1));
        Assert.Equal(HeldReleaseVerdict.Release, HeldPaymentRules.Release(failed, Now));

        var waiting = Facts(status: ServicePaymentStatus.TransferFailed, nextAttemptAt: Now.AddMinutes(10));
        Assert.Equal(HeldReleaseVerdict.NotYet, HeldPaymentRules.Release(waiting, Now));
    }

    [Fact]
    public void Release_ARefundInProgressStopsTheRelease_ThenTheRestIsReleased()
    {
        Assert.Equal(HeldReleaseVerdict.NotYet, HeldPaymentRules.Release(Facts(inProgress: 1_000, confirmed: true), Now));

        Assert.Equal(HeldReleaseVerdict.Release, HeldPaymentRules.Release(Facts(refunded: 2_000), Now));

        Assert.Equal(HeldReleaseVerdict.NothingToRelease, HeldPaymentRules.Release(Facts(refunded: 9_000), Now));
    }

    [Fact]
    public void Release_WaitsForTheMomentTheMoneyIsDue_UnlessThePayerSaidAllFine()
    {
        var early = Facts(releaseAt: Now.AddMinutes(30));
        Assert.Equal(HeldReleaseVerdict.NotYet, HeldPaymentRules.Release(early, Now));
        Assert.Equal(HeldReleaseVerdict.Release, HeldPaymentRules.Release(early with { ConfirmedByPayer = true }, Now));

        var notDone = Facts() with { ReleaseAt = null };
        Assert.Equal(HeldReleaseVerdict.NotYet, HeldPaymentRules.Release(notDone, Now));

        Assert.Equal(HeldReleaseVerdict.Release, HeldPaymentRules.Release(Facts(releaseAt: Now), Now));
    }

    [Fact]
    public void Release_ANextAttemptInTheFutureWaits_EvenForThePayersWord()
    {
        var facts = Facts(nextAttemptAt: Now.AddMinutes(5), confirmed: true);

        Assert.Equal(HeldReleaseVerdict.NotYet, HeldPaymentRules.Release(facts, Now));
        Assert.Equal(HeldReleaseVerdict.Release, HeldPaymentRules.Release(facts, Now.AddMinutes(5)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 120)]
    [InlineData(5, 240)]
    [InlineData(6, 480)]
    [InlineData(7, 720)]
    [InlineData(40, 720)]
    [InlineData(1_000, 720)]
    public void TransferBackoff_DoublesFromTheBase_UpToTheMaximum(int failures, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), HeldPaymentRules.TransferBackoff(failures, 15, 720));

    [Fact]
    public void TransferBackoff_IsNeverShorterThanAMinute() =>
        Assert.Equal(TimeSpan.FromMinutes(1), HeldPaymentRules.TransferBackoff(1, 0, 10));

    [Fact]
    public void AuthorizationExpiry_IsTheConfiguredValidityAfterTheBlock_FarBelowWhatCardsKeep()
    {
        var authorized = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(authorized.AddHours(48), HeldPaymentRules.AuthorizationExpiry(authorized, TimeSpan.FromHours(48)));
    }

    [Fact]
    public void CanCapture_OnlyAnUnexpiredBlock()
    {
        var expires = Now.AddHours(1);

        Assert.True(HeldPaymentRules.CanCapture(ServicePaymentStatus.Authorized, expires, Now));
        Assert.True(HeldPaymentRules.CanCapture(ServicePaymentStatus.Authorized, null, Now));

        Assert.False(HeldPaymentRules.CanCapture(ServicePaymentStatus.Authorized, expires, expires));
        Assert.False(HeldPaymentRules.CanCapture(ServicePaymentStatus.Authorized, expires, expires.AddMinutes(1)));

        Assert.False(HeldPaymentRules.CanCapture(ServicePaymentStatus.Held, expires, Now));
        Assert.False(HeldPaymentRules.CanCapture(ServicePaymentStatus.Canceled, expires, Now));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AuthorizationLapsed_FollowsTheExpiry(bool past, bool lapsed)
    {
        DateTime? expiresAt = past ? Now.AddSeconds(-1) : Now.AddSeconds(1);

        Assert.Equal(lapsed, HeldPaymentRules.AuthorizationLapsed(expiresAt, Now));
        Assert.False(HeldPaymentRules.AuthorizationLapsed(null, Now));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Held, 10_000, 9_000, 0, false, ServicePaymentStatus.Held)]
    [InlineData(ServicePaymentStatus.Held, 10_000, 9_000, 10_000, false, ServicePaymentStatus.Refunded)]
    [InlineData(ServicePaymentStatus.Disputed, 10_000, 9_000, 9_000, false, ServicePaymentStatus.PartiallyRefunded)]
    [InlineData(ServicePaymentStatus.Held, 10_000, 9_000, 2_000, false, ServicePaymentStatus.Held)]
    [InlineData(ServicePaymentStatus.TransferFailed, 10_000, 9_000, 2_000, false, ServicePaymentStatus.TransferFailed)]
    [InlineData(ServicePaymentStatus.Refunded, 10_000, 9_000, 0, false, ServicePaymentStatus.Held)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, 10_000, 9_000, 2_000, false, ServicePaymentStatus.Held)]
    [InlineData(ServicePaymentStatus.Held, 10_000, 9_000, 10_000, true, ServicePaymentStatus.Held)]
    [InlineData(ServicePaymentStatus.Released, 10_000, 9_000, 10_000, true, ServicePaymentStatus.Released)]
    [InlineData(ServicePaymentStatus.Authorized, 10_000, 9_000, 10_000, false, ServicePaymentStatus.Authorized)]
    [InlineData(ServicePaymentStatus.NeedsReview, 10_000, 9_000, 10_000, false, ServicePaymentStatus.NeedsReview)]
    public void StatusAfterRefunds_FollowsWhatIsLeftForTheSupplier(
        ServicePaymentStatus current, int amount, int net, int refunded, bool hasTransfer, ServicePaymentStatus expected) =>
        Assert.Equal(expected, HeldPaymentRules.StatusAfterRefunds(current, amount, net, refunded, hasTransfer));

    [Fact]
    public void TheStatesOfTheHeldCycle_AreTheFiveNewOnes_AndOnlyThoseHoldMoney()
    {
        var cycle = Enum.GetValues<ServicePaymentStatus>().Where(HeldPaymentRules.IsHeldCycleStatus).ToArray();
        Assert.Equal(
            [ServicePaymentStatus.Authorized, ServicePaymentStatus.Held, ServicePaymentStatus.Released, ServicePaymentStatus.Disputed, ServicePaymentStatus.TransferFailed],
            cycle);

        Assert.False(HeldPaymentRules.IsMoneyHeld(ServicePaymentStatus.Released));
        Assert.False(HeldPaymentRules.IsMoneyHeld(ServicePaymentStatus.Authorized));
        Assert.False(HeldPaymentRules.IsMoneyHeld(ServicePaymentStatus.Refunded));
        Assert.True(HeldPaymentRules.IsMoneyHeld(ServicePaymentStatus.Held));
        Assert.True(HeldPaymentRules.IsMoneyHeld(ServicePaymentStatus.Disputed));
        Assert.True(HeldPaymentRules.IsMoneyHeld(ServicePaymentStatus.TransferFailed));

        Assert.True(HeldPaymentRules.IsOpen(ServicePaymentStatus.Authorized));
        Assert.False(HeldPaymentRules.IsOpen(ServicePaymentStatus.Released));
        Assert.False(HeldPaymentRules.IsOpen(ServicePaymentStatus.Canceled));
    }

    [Fact]
    public void TheStatusNumbers_AreAppendOnly()
    {
        Assert.Equal(7, (int)ServicePaymentStatus.NeedsReview);
        Assert.Equal(8, (int)ServicePaymentStatus.Authorized);
        Assert.Equal(9, (int)ServicePaymentStatus.Held);
        Assert.Equal(10, (int)ServicePaymentStatus.Released);
        Assert.Equal(11, (int)ServicePaymentStatus.Disputed);
        Assert.Equal(12, (int)ServicePaymentStatus.TransferFailed);
    }

    [Theory]
    [InlineData(52, false)]
    [InlineData(53, true)]
    [InlineData(60, true)]
    public void IsNearMaxHold_WarnsSevenDaysBeforeTheMaximum(int daysHeld, bool near)
    {
        var captured = Now.AddDays(-daysHeld);

        Assert.Equal(near, HeldPaymentRules.IsNearMaxHold(captured, Now, maxHoldDays: 60, warningDays: 7));
        Assert.False(HeldPaymentRules.IsNearMaxHold(null, Now, 60, 7));
    }
}
