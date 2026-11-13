using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T01 (NF-52, CHK-12): when the three new stay stages are due. Check-in 5 October 2026 (Europe/Rome on summer time, UTC+2) unless stated:
/// the receipt that is missing the day after the communication at 10:00, a short stay not entrusted from 6 hours before the arrival, a stay over 30 nights
/// the day before the arrival at 09:00. Each is dropped 24 hours after its time, never sent as a backlog.
/// </summary>
public class StayAlertScheduleChk12Tests
{
    private static readonly StayAlertOptions Defaults = new();
    private static readonly DateTime CheckIn = Utc(2026, 10, 5);

    // ─── Receipt missing ───

    [Fact]
    public void DueReceiptMissingStep_TheDayAfterTheCommunicationAt10Rome_IsDueOnce()
    {
        var communicated = Utc(2026, 10, 5, 14, 0);

        Assert.Null(ReceiptDue(communicated, Utc(2026, 10, 6, 7, 59)));

        var step = ReceiptDue(communicated, Utc(2026, 10, 6, 8, 0));
        Assert.NotNull(step);
        Assert.Equal(StayAlertType.ReceiptMissing, step.Type);
        Assert.Equal(StayAlertSchedule.SingleStage, step.Stage);
        Assert.Equal(CheckIn, step.ReferenceDate);
        Assert.Equal(StayAlertKind.ReceiptMissing, step.Alert.Kind);
        Assert.Null(step.Alert.DeadlineUtc);
    }

    [Fact]
    public void DueReceiptMissingStep_ADayAfterItsTime_IsDroppedNotSentAsABacklog()
    {
        var communicated = Utc(2026, 10, 5, 14, 0);

        Assert.NotNull(ReceiptDue(communicated, Utc(2026, 10, 7, 7, 59)));
        Assert.Null(ReceiptDue(communicated, Utc(2026, 10, 7, 8, 0)));
        Assert.Null(ReceiptDue(communicated, Utc(2026, 10, 12, 8, 0)));
    }

    [Fact]
    public void DueReceiptMissingStep_ACommunicationAfterMidnightInRome_CountsTheRomeDay()
    {
        // 22:30 UTC of the 5th is 00:30 of the 6th in Rome: the day after is the 7th.
        var communicated = Utc(2026, 10, 5, 22, 30);

        Assert.Null(ReceiptDue(communicated, Utc(2026, 10, 6, 8, 0)));
        Assert.NotNull(ReceiptDue(communicated, Utc(2026, 10, 7, 8, 0)));
    }

    [Fact]
    public void DueReceiptMissingStep_ADeclarationStoredAsMidnightUtc_CountsItsOwnDay()
    {
        // The day a host declared it sent is stored as midnight UTC of the Rome day: the same calendar day.
        Assert.NotNull(ReceiptDue(Utc(2026, 10, 5), Utc(2026, 10, 6, 8, 0)));
        Assert.Null(ReceiptDue(Utc(2026, 10, 5), Utc(2026, 10, 6, 7, 59)));
    }

    [Fact]
    public void DueReceiptMissingStep_TheHourIsAnOption()
    {
        var options = new StayAlertOptions { ReceiptMissingHourLocal = 18 };

        Assert.Null(StayAlertSchedule.DueReceiptMissingStep(Stay(), Utc(2026, 10, 5, 14, 0), options, Utc(2026, 10, 6, 15, 59)));
        Assert.NotNull(StayAlertSchedule.DueReceiptMissingStep(Stay(), Utc(2026, 10, 5, 14, 0), options, Utc(2026, 10, 6, 16, 0)));
    }

    // ─── Short stay at risk ───

    [Fact]
    public void DueShortStayRiskStep_NotEntrusted_IsDueFromSixHoursBeforeTheArrivalUntilTheLegalDeadline()
    {
        var shortStay = Stay(checkOut: Utc(2026, 10, 6));

        // The arrival day starts at 22:00 UTC of the 4th: six hours before it, the stay is at risk; the 6-hour term ends at 04:00 UTC of the 5th.
        Assert.Null(RiskDue(shortStay, entrusted: false, Utc(2026, 10, 4, 15, 59)));

        var step = RiskDue(shortStay, entrusted: false, Utc(2026, 10, 4, 16, 0));
        Assert.NotNull(step);
        Assert.Equal(StayAlertType.ShortStayAtRisk, step.Type);
        Assert.Equal(StayAlertSchedule.SingleStage, step.Stage);
        Assert.Equal(StayAlertKind.ShortStayAtRisk, step.Alert.Kind);
        Assert.True(step.Alert.ShortStay);
        Assert.Null(step.Alert.DeadlineUtc);
        Assert.NotNull(RiskDue(shortStay, false, Utc(2026, 10, 5, 3, 59)));

        // After the legal deadline the overdue stage takes over.
        Assert.Null(RiskDue(shortStay, false, Utc(2026, 10, 5, 4, 0)));
    }

    [Fact]
    public void DueShortStayRiskStep_GuestsEntrusted_OrMoreThanOneNight_ReturnsNothing()
    {
        Assert.Null(RiskDue(Stay(checkOut: Utc(2026, 10, 6)), entrusted: true, Utc(2026, 10, 4, 20, 0)));
        Assert.Null(RiskDue(Stay(checkOut: Utc(2026, 10, 7)), entrusted: false, Utc(2026, 10, 4, 20, 0)));
    }

    [Fact]
    public void DueShortStayRiskStep_ARegisteredArrival_UsesItAndShowsTheDeadline()
    {
        var arrived = Stay(checkOut: Utc(2026, 10, 6), arrivedAt: Utc(2026, 10, 5, 13, 0));

        Assert.Null(RiskDue(arrived, false, Utc(2026, 10, 5, 6, 59)));

        var step = RiskDue(arrived, false, Utc(2026, 10, 5, 7, 0));
        Assert.NotNull(step);
        Assert.Equal(Utc(2026, 10, 5, 19, 0), step.Alert.DeadlineUtc);
        Assert.Null(RiskDue(arrived, false, Utc(2026, 10, 5, 19, 0)));
    }

    [Fact]
    public void DueShortStayRiskStep_TheHoursAreAnOption()
    {
        var options = new StayAlertOptions { ShortStayRiskHours = 0 };
        var shortStay = Stay(checkOut: Utc(2026, 10, 6));

        Assert.Null(StayAlertSchedule.DueShortStayRiskStep(shortStay, false, options, Utc(2026, 10, 4, 21, 59)));
        Assert.NotNull(StayAlertSchedule.DueShortStayRiskStep(shortStay, false, options, Utc(2026, 10, 4, 22, 0)));
    }

    // ─── Over thirty nights ───

    [Fact]
    public void DueOverThirtyNightsStep_TheDayBeforeTheArrivalAt9Rome_IsDueOnce()
    {
        var longStay = Stay(checkOut: Utc(2026, 11, 5)); // 31 nights

        Assert.Null(OverDue(longStay, Utc(2026, 10, 4, 6, 59)));

        var step = OverDue(longStay, Utc(2026, 10, 4, 7, 0));
        Assert.NotNull(step);
        Assert.Equal(StayAlertType.OverThirtyNights, step.Type);
        Assert.Equal(StayAlertSchedule.SingleStage, step.Stage);
        Assert.Equal(StayAlertKind.OverThirtyNights, step.Alert.Kind);
        Assert.Equal(CheckIn, step.ReferenceDate);
        Assert.NotNull(OverDue(longStay, Utc(2026, 10, 5, 6, 59)));
        Assert.Null(OverDue(longStay, Utc(2026, 10, 5, 7, 0)));
    }

    [Fact]
    public void DueOverThirtyNightsStep_ThirtyNightsOrFewer_ReturnsNothing()
    {
        Assert.Null(OverDue(Stay(checkOut: Utc(2026, 11, 4)), Utc(2026, 10, 4, 8, 0))); // 30 nights
        Assert.Null(OverDue(Stay(checkOut: Utc(2026, 10, 8)), Utc(2026, 10, 4, 8, 0)));
    }

    [Fact]
    public void DueOverThirtyNightsStep_TheHourIsAnOption()
    {
        var options = new StayAlertOptions { OverThirtyNightsHourLocal = 20 };

        Assert.Null(StayAlertSchedule.DueOverThirtyNightsStep(Stay(checkOut: Utc(2026, 11, 5)), options, Utc(2026, 10, 4, 17, 59)));
        Assert.NotNull(StayAlertSchedule.DueOverThirtyNightsStep(Stay(checkOut: Utc(2026, 11, 5)), options, Utc(2026, 10, 4, 18, 0)));
    }

    // ─── Add-only ───

    [Fact]
    public void StayAlertType_TheStoredValuesAreUnchanged_AndTheNewOnesAreAppended()
    {
        Assert.Equal(1, (int)StayAlertType.AlloggiatiDeadline);
        Assert.Equal(2, (int)StayAlertType.AlloggiatiFailed);
        Assert.Equal(3, (int)StayAlertType.CheckoutReminder);
        Assert.Equal(4, (int)StayAlertType.ReceiptMissing);
        Assert.Equal(5, (int)StayAlertType.ShortStayAtRisk);
        Assert.Equal(6, (int)StayAlertType.OverThirtyNights);
    }

    [Fact]
    public void StayAlertKind_TheExistingKindsKeepTheirNumbers_AndTheNewOnesFollow()
    {
        StayAlertKind[] inOrder =
        [
            StayAlertKind.GuestDataMissing,
            StayAlertKind.AlloggiatiDeadlineApproaching,
            StayAlertKind.AlloggiatiOverdue,
            StayAlertKind.AlloggiatiFailed,
            StayAlertKind.CheckoutReminder,
            StayAlertKind.ReceiptMissing,
            StayAlertKind.ShortStayAtRisk,
            StayAlertKind.OverThirtyNights,
        ];

        Assert.Equal(inOrder, Enum.GetValues<StayAlertKind>().OrderBy(kind => (int)kind).ToArray());
        for (var number = 0; number < inOrder.Length; number++)
            Assert.Equal(number, (int)inOrder[number]);
    }

    private static StayAlertStep? ReceiptDue(DateTime communicatedAtUtc, DateTime nowUtc) =>
        StayAlertSchedule.DueReceiptMissingStep(Stay(), communicatedAtUtc, Defaults, nowUtc);

    private static StayAlertStep? RiskDue(Booking booking, bool entrusted, DateTime nowUtc) =>
        StayAlertSchedule.DueShortStayRiskStep(booking, entrusted, Defaults, nowUtc);

    private static StayAlertStep? OverDue(Booking booking, DateTime nowUtc) =>
        StayAlertSchedule.DueOverThirtyNightsStep(booking, Defaults, nowUtc);

    private static Booking Stay(DateTime? checkOut = null, DateTime? arrivedAt = null) => new()
    {
        CheckInDate = CheckIn,
        CheckOutDate = checkOut ?? Utc(2026, 10, 8),
        ArrivedAt = arrivedAt,
        Status = BookingStatus.Confirmed,
    };

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
