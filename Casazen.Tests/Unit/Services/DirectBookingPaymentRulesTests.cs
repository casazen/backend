using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-07 (A3-16): "Paga alla scadenza" offered only when its charge day is ahead, the deadline from the property's
/// cancellation policy when it has one, and a free cancellation promised only when the policy really refunds in full (DB-08a).
/// </summary>
public class DirectBookingPaymentRulesTests
{
    private static readonly DateTime Today = Utc(2026, 9, 24);

    [Fact]
    public void FreeRefundDeadline_WithoutPolicy_IsSevenDaysBeforeCheckIn()
    {
        Assert.Equal(Utc(2026, 10, 3), DirectBookingPaymentRules.FreeRefundDeadline(Utc(2026, 10, 10), null));
    }

    [Theory]
    // Full refund until 48 h before the start of the check-in day (10 Oct 00:00 Rome): the last whole day is 7 Oct.
    [InlineData(48, "2026-10-07")]
    // 50 h before is 7 Oct at 22:00: 7 Oct is no longer entirely free, the last whole day is 6 Oct.
    [InlineData(50, "2026-10-06")]
    // 30 days before.
    [InlineData(30 * 24, "2026-09-09")]
    // Full refund up to the check-in: the day before.
    [InlineData(0, "2026-10-09")]
    public void FreeRefundDeadline_WithPolicy_IsTheLastWholeDayOfTheFullRefund(int fullRefundHours, string expected)
    {
        var policy = new CancellationPolicy { Name = "Test", FullRefundHours = fullRefundHours };

        var deadline = DirectBookingPaymentRules.FreeRefundDeadline(Utc(2026, 10, 10), policy);

        Assert.Equal(expected, deadline.ToString("yyyy-MM-dd"));
        Assert.Equal(DateTimeKind.Utc, deadline.Kind);
        Assert.Equal(TimeSpan.Zero, deadline.TimeOfDay);
    }

    [Fact]
    public void FreeRefundDeadline_PolicyAcrossTheSwitchToSummerTime_NeverPromisesAnHourTooMuch()
    {
        // 48 h before 31 Mar 2026 00:00 (CEST, 30 Mar 22:00 UTC) is 28 Mar 22:00 UTC = 28 Mar 23:00 in Rome (CET): at
        // 23:30 of 28 Mar only 47.5 h are left, so the last whole day of full refund is 27 Mar.
        var policy = new CancellationPolicy { Name = "Test", FullRefundHours = 48 };

        Assert.Equal(Utc(2026, 3, 27), DirectBookingPaymentRules.FreeRefundDeadline(Utc(2026, 3, 31), policy));
    }

    [Fact]
    public void FreeRefundDeadline_PolicyDeadline_AgreesWithTheRefundPolicy()
    {
        var policy = new CancellationPolicy { Name = "Moderata", FullRefundHours = 72, PartialRefundHours = 24, PartialRefundPercent = 50m };
        var checkIn = Utc(2026, 10, 20);
        var deadline = DirectBookingPaymentRules.FreeRefundDeadline(checkIn, policy);
        var booking = new Booking { CheckInDate = checkIn, FreeRefundDeadline = deadline };

        // 23:59 in Rome (21:59 UTC, CEST) of the deadline day: still a full refund under the policy alone.
        var lastMinute = new DateTimeOffset(deadline.Year, deadline.Month, deadline.Day, 21, 59, 0, TimeSpan.Zero);
        var floor = CancellationRefundPolicy.Evaluate(new Booking { CheckInDate = checkIn }, policy, lastMinute);
        Assert.Equal(100m, floor.Percent);

        // The day after, the deadline rule no longer applies and the policy gives less.
        var nextDay = lastMinute.AddHours(12);
        Assert.Equal(50m, CancellationRefundPolicy.Evaluate(booking, policy, nextDay).Percent);
    }

    [Fact]
    public void OptionsFor_ArrivalTomorrowForTenNights_DoesNotOfferTheDeferredPayment()
    {
        var checkIn = Today.AddDays(1);

        var options = DirectBookingPaymentRules.OptionsFor(DirectBookingPaymentRules.FreeRefundDeadline(checkIn, null), Today);

        Assert.False(options.DeferredPaymentAvailable);
        Assert.Null(options.DeferredChargeDate);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(30, true)]
    public void IsDeferredPaymentOffered_WithoutPolicy_OnlyForAnArrivalMoreThanSevenDaysAway(int daysToArrival, bool offered)
    {
        var deadline = DirectBookingPaymentRules.FreeRefundDeadline(Today.AddDays(daysToArrival), null);

        Assert.Equal(offered, DirectBookingPaymentRules.IsDeferredPaymentOffered(deadline, Today));
    }

    [Fact]
    public void OptionsFor_ArrivalInThirtyDays_OffersTheDeferredPaymentOnTheDeadline()
    {
        var deadline = DirectBookingPaymentRules.FreeRefundDeadline(Today.AddDays(30), null);

        var options = DirectBookingPaymentRules.OptionsFor(deadline, Today);

        Assert.True(options.DeferredPaymentAvailable);
        Assert.Equal(Today.AddDays(23), options.DeferredChargeDate);
    }

    [Fact]
    public void OptionsFor_FreeCancellationIsPromisedOnlyWhenTheStaysPolicyRefundsInFullUntilADayAhead()
    {
        // DB-08a: the guest cancels by themselves, so the checkout may say "gratuita fino al …", but only when it is true.
        var policy = new CancellationPolicy { Name = "Moderata", FullRefundHours = 5 * 24 };
        var neverFull = new CancellationPolicy { Name = "Non rimborsabile", FullRefundHours = CancellationRefundPolicy.NeverFullRefundHours };
        var deadline = Today.AddDays(60);

        Assert.Equal(deadline, DirectBookingPaymentRules.OptionsFor(deadline, Today, policy).FreeCancellationUntil);
        Assert.Equal(Today, DirectBookingPaymentRules.OptionsFor(Today, Today, policy).FreeCancellationUntil); // today is still free
        Assert.Null(DirectBookingPaymentRules.OptionsFor(Today.AddDays(-1), Today, policy).FreeCancellationUntil); // the day is past
        Assert.Null(DirectBookingPaymentRules.OptionsFor(deadline, Today).FreeCancellationUntil); // no policy
        Assert.Null(DirectBookingPaymentRules.OptionsFor(deadline, Today, neverFull).FreeCancellationUntil); // never refunded in full
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
