using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-02 (#51): minimum refund of a cancellation from the rules already in the model, never an invented one.
/// </summary>
public class CancellationRefundPolicyTests
{
    private static readonly DateTime CheckIn = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_OnLastFreeCancellationDayInRome_GrantsFullRefund()
    {
        var booking = new Booking { CheckInDate = CheckIn, FreeRefundDeadline = CheckIn.AddDays(-7) };

        // 23:30 in Rome on the deadline day (21:30 UTC, CEST).
        var floor = CancellationRefundPolicy.Evaluate(booking, null, new DateTimeOffset(2026, 10, 13, 21, 30, 0, TimeSpan.Zero));

        Assert.Equal(CancellationRefundRule.FreeCancellationDeadline, floor.Rule);
        Assert.Equal(100m, floor.Percent);
    }

    [Fact]
    public void Evaluate_AfterFreeCancellationDeadlineWithoutPolicy_HasNoRule()
    {
        var booking = new Booking { CheckInDate = CheckIn, FreeRefundDeadline = CheckIn.AddDays(-7) };

        // 00:30 in Rome the day after the deadline.
        var floor = CancellationRefundPolicy.Evaluate(booking, null, new DateTimeOffset(2026, 10, 13, 22, 30, 0, TimeSpan.Zero));

        Assert.Equal(CancellationRefundRule.None, floor.Rule);
        Assert.Null(floor.Percent);
        Assert.Equal(0m, CancellationRefundPolicy.MinimumRefund(floor, 500m, 0m));
    }

    [Theory]
    [InlineData(72, 100)]
    [InlineData(48, 50)]
    [InlineData(47, 0)]
    public void Evaluate_PropertyPolicy_UsesHoursBeforeStartOfCheckInDay(int hoursBefore, int expectedPercent)
    {
        var booking = new Booking { CheckInDate = CheckIn };
        var policy = new CancellationPolicy { Name = "Moderata", FullRefundHours = 72, PartialRefundHours = 48, PartialRefundPercent = 50m };
        // The check-in day starts at 22:00 UTC of the day before (CEST).
        var startOfCheckInDay = new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero);

        var floor = CancellationRefundPolicy.Evaluate(booking, policy, startOfCheckInDay.AddHours(-hoursBefore));

        Assert.Equal(CancellationRefundRule.PropertyCancellationPolicy, floor.Rule);
        Assert.Equal(expectedPercent, floor.Percent);
    }

    [Fact]
    public void Evaluate_DeadlineAndStricterPolicy_KeepsTheLargerRefund()
    {
        var booking = new Booking { CheckInDate = CheckIn, FreeRefundDeadline = CheckIn.AddDays(-7) };
        var strict = new CancellationPolicy { Name = "Rigida", FullRefundHours = 14 * 24, PartialRefundHours = 7 * 24, PartialRefundPercent = 50m };

        var floor = CancellationRefundPolicy.Evaluate(booking, strict, new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(CancellationRefundRule.FreeCancellationDeadline, floor.Rule);
        Assert.Equal(100m, floor.Percent);
    }

    [Fact]
    public void MinimumRefund_PartOfPaidAlreadyRefunded_DeductsIt()
    {
        var floor = new CancellationRefundFloor(CancellationRefundRule.PropertyCancellationPolicy, 50m);

        Assert.Equal(75m, CancellationRefundPolicy.MinimumRefund(floor, 301m, 75.5m));
        Assert.Equal(0m, CancellationRefundPolicy.MinimumRefund(floor, 100m, 80m));
    }
}
