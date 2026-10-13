using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CO-10: when each stay alert is due. Check-in 5 October 2026 (Europe/Rome on summer time, UTC+2) unless stated:
/// guest data the day before at 10:00, deadline approaching at 12:00 of the arrival day, overdue at the end of the arrival
/// day, daily reminders at 09:00 of the following days.
/// </summary>
public class StayAlertScheduleTests
{
    private static readonly StayAlertOptions Defaults = new();
    private static readonly DateTime CheckIn = Utc(2026, 10, 5);

    [Fact]
    public void DueAlloggiatiStep_IncompleteDataBeforeTheDayBeforeAt10_ReturnsNothing()
    {
        var step = Due(Stay(), guestDataComplete: false, Utc(2026, 10, 4, 7, 59));

        Assert.Null(step);
    }

    [Fact]
    public void DueAlloggiatiStep_IncompleteDataTheDayBeforeAt10Rome_ReturnsGuestDataMissing()
    {
        var step = Due(Stay(), guestDataComplete: false, Utc(2026, 10, 4, 8, 0));

        Assert.NotNull(step);
        Assert.Equal(StayAlertType.AlloggiatiDeadline, step.Type);
        Assert.Equal(AlloggiatiAlertStage.GuestDataMissing, step.Stage);
        Assert.Equal(StayAlertKind.GuestDataMissing, step.Alert.Kind);
        Assert.Equal(CheckIn, step.ReferenceDate);
    }

    [Fact]
    public void DueAlloggiatiStep_CompleteDataTheDayBefore_ReturnsNothing()
    {
        Assert.Null(Due(Stay(), guestDataComplete: true, Utc(2026, 10, 4, 20, 0)));
    }

    [Fact]
    public void DueAlloggiatiStep_ArrivalDayAtNoonRome_ReturnsDeadlineApproachingWithoutDeadlineWhenArrivalNotRegistered()
    {
        var step = Due(Stay(), guestDataComplete: true, Utc(2026, 10, 5, 10, 0));

        Assert.NotNull(step);
        Assert.Equal(AlloggiatiAlertStage.DeadlineApproaching, step.Stage);
        Assert.Equal(StayAlertKind.AlloggiatiDeadlineApproaching, step.Alert.Kind);
        Assert.Null(step.Alert.DeadlineUtc);
        Assert.False(step.Alert.ShortStay);
    }

    [Fact]
    public void DueAlloggiatiStep_IncompleteDataOnTheArrivalDayAfterNoon_ReturnsTheHigherStageOnly()
    {
        var step = Due(Stay(), guestDataComplete: false, Utc(2026, 10, 5, 15, 0));

        Assert.Equal(AlloggiatiAlertStage.DeadlineApproaching, step!.Stage);
    }

    [Fact]
    public void DueAlloggiatiStep_EndOfTheArrivalDayInRome_ReturnsOverdue()
    {
        Assert.Equal(AlloggiatiAlertStage.DeadlineApproaching, Due(Stay(), true, Utc(2026, 10, 5, 21, 59))!.Stage);

        var step = Due(Stay(), guestDataComplete: true, Utc(2026, 10, 5, 22, 0));

        Assert.Equal(AlloggiatiAlertStage.Overdue, step!.Stage);
        Assert.Equal(StayAlertKind.AlloggiatiOverdue, step.Alert.Kind);
        Assert.Equal(0, step.Alert.ReminderNumber);
    }

    [Fact]
    public void DueAlloggiatiStep_ShortStayWithoutRegisteredArrival_IsNotOverdueBeforeTheEndOfTheArrivalDay()
    {
        // The deadline shown by CasaZen (CO-11) is 06:00 of the arrival day, before any real arrival: no false alarm.
        var shortStay = Stay(checkOut: Utc(2026, 10, 6));

        Assert.Null(Due(shortStay, guestDataComplete: true, Utc(2026, 10, 5, 5, 0)));
        Assert.Equal(AlloggiatiAlertStage.DeadlineApproaching, Due(shortStay, true, Utc(2026, 10, 5, 10, 0))!.Stage);
        Assert.True(Due(shortStay, true, Utc(2026, 10, 5, 10, 0))!.Alert.ShortStay);
        Assert.Equal(AlloggiatiAlertStage.Overdue, Due(shortStay, true, Utc(2026, 10, 5, 22, 0))!.Stage);
    }

    [Fact]
    public void DueAlloggiatiStep_ShortStayWithRegisteredArrival_UsesTheSixHourLegalDeadline()
    {
        var arrived = Stay(checkOut: Utc(2026, 10, 6), arrivedAt: Utc(2026, 10, 5, 13, 0));

        var approaching = Due(arrived, true, Utc(2026, 10, 5, 13, 0));
        var overdue = Due(arrived, true, Utc(2026, 10, 5, 19, 0));

        Assert.Equal(AlloggiatiAlertStage.DeadlineApproaching, approaching!.Stage);
        Assert.Equal(Utc(2026, 10, 5, 19, 0), approaching.Alert.DeadlineUtc);
        Assert.Equal(AlloggiatiAlertStage.Overdue, overdue!.Stage);
        Assert.Equal(AlloggiatiAlertStage.DeadlineApproaching, Due(arrived, true, Utc(2026, 10, 5, 18, 59))!.Stage);
    }

    [Fact]
    public void DueAlloggiatiStep_RegisteredArrival_OverdueTwentyFourHoursLater()
    {
        var arrived = Stay(arrivedAt: Utc(2026, 10, 5, 13, 0));

        Assert.Equal(AlloggiatiAlertStage.DeadlineApproaching, Due(arrived, true, Utc(2026, 10, 6, 12, 59))!.Stage);
        Assert.Equal(AlloggiatiAlertStage.Overdue, Due(arrived, true, Utc(2026, 10, 6, 13, 0))!.Stage);
    }

    [Fact]
    public void DueAlloggiatiStep_FollowingDaysAt9Rome_ReturnsTheDailyRemindersUpToTheMaximum()
    {
        var first = Due(Stay(), true, Utc(2026, 10, 7, 7, 0));
        var second = Due(Stay(), true, Utc(2026, 10, 8, 7, 0));

        Assert.Equal(AlloggiatiAlertStage.Overdue, Due(Stay(), true, Utc(2026, 10, 7, 6, 59))!.Stage);
        Assert.Equal(AlloggiatiAlertStage.OverdueReminder(1), first!.Stage);
        Assert.Equal(1, first.Alert.ReminderNumber);
        Assert.Equal(2, first.Alert.MaxReminders);
        Assert.Equal(AlloggiatiAlertStage.OverdueReminder(2), second!.Stage);
        Assert.Equal(StayAlertKind.AlloggiatiOverdue, second.Alert.Kind);
    }

    [Fact]
    public void DueAlloggiatiStep_ADayAfterTheLastReminder_ReturnsNothing()
    {
        Assert.NotNull(Due(Stay(), true, Utc(2026, 10, 9, 6, 59)));
        Assert.Null(Due(Stay(), true, Utc(2026, 10, 9, 7, 0)));
    }

    [Fact]
    public void DueAlloggiatiStep_NoDailyReminders_OverdueOnlyForADay()
    {
        var options = new StayAlertOptions { MaxOverdueReminders = 0 };

        Assert.Equal(AlloggiatiAlertStage.Overdue, StayAlertSchedule.DueAlloggiatiStep(Stay(), true, options, Utc(2026, 10, 6, 21, 59))!.Stage);
        Assert.Null(StayAlertSchedule.DueAlloggiatiStep(Stay(), true, options, Utc(2026, 10, 6, 22, 0)));
        Assert.Equal(3, options.MaxAlloggiatiDeadlineMessages);
        Assert.Equal(5, Defaults.MaxAlloggiatiDeadlineMessages);
    }

    [Fact]
    public void DeadlineWarningAtUtc_WarningBeforeTheArrivalDay_IsTheStartOfTheArrivalDay()
    {
        var warning = StayAlertSchedule.DeadlineWarningAtUtc(CheckIn, Utc(2026, 10, 5, 22, 0), warningHours: 30);

        Assert.Equal(Utc(2026, 10, 4, 22, 0), warning);
    }

    [Fact]
    public void GuestDataReminderAtUtc_WinterTime_IsTenInRome()
    {
        Assert.Equal(Utc(2026, 12, 9, 9, 0), StayAlertSchedule.GuestDataReminderAtUtc(Utc(2026, 12, 10), 10));
    }

    [Theory]
    [InlineData("Europe/Rome", 2026, 10, 8, 18)]
    [InlineData(null, 2026, 10, 8, 18)]
    [InlineData("", 2026, 10, 8, 18)]
    [InlineData("Not/AZone", 2026, 10, 8, 18)]
    [InlineData("America/New_York", 2026, 10, 9, 0)]
    public void CheckoutReminderAtUtc_PropertyTimeZone_IsTheLocalHourOfTheCheckOutDay(
        string? zone, int year, int month, int day, int hourUtc)
    {
        var reminder = StayAlertSchedule.CheckoutReminderAtUtc(Utc(2026, 10, 8), zone, 20);

        Assert.Equal(Utc(year, month, day, hourUtc, 0), reminder);
    }

    [Fact]
    public void CheckoutReminderAtUtc_HourSkippedByDaylightSaving_IsOneHourLater()
    {
        // 29 March 2026: in Rome 02:00 does not exist (02:00 -> 03:00 CEST = 01:00 UTC).
        Assert.Equal(Utc(2026, 3, 29, 1, 0), StayAlertSchedule.CheckoutReminderAtUtc(Utc(2026, 3, 29), "Europe/Rome", 2));
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.CheckedIn, true)]
    [InlineData(BookingStatus.Pending, false)]
    [InlineData(BookingStatus.CheckedOut, false)]
    [InlineData(BookingStatus.Cancelled, false)]
    public void DueCheckoutReminder_AtTheHour_OnlyForStaysConfirmedOrCheckedIn(BookingStatus status, bool due)
    {
        var booking = Stay(checkOut: Utc(2026, 10, 8), status: status);

        var step = StayAlertSchedule.DueCheckoutReminder(booking, "Europe/Rome", 20, Utc(2026, 10, 8, 18, 0));

        Assert.Equal(due, step is not null);
    }

    [Fact]
    public void DueCheckoutReminder_BeforeTheHourOrTwelveHoursLater_IsNotDue()
    {
        var booking = Stay(checkOut: Utc(2026, 10, 8));

        Assert.Null(StayAlertSchedule.DueCheckoutReminder(booking, null, 20, Utc(2026, 10, 8, 17, 59)));
        Assert.Null(StayAlertSchedule.DueCheckoutReminder(booking, null, 20, Utc(2026, 10, 9, 6, 0)));
        var step = StayAlertSchedule.DueCheckoutReminder(booking, null, 20, Utc(2026, 10, 9, 5, 59));
        Assert.NotNull(step);
        Assert.Equal(StayAlertType.CheckoutReminder, step.Type);
        Assert.Equal(Utc(2026, 10, 8), step.ReferenceDate);
        Assert.Equal(StayAlertKind.CheckoutReminder, step.Alert.Kind);
    }

    private static StayAlertStep? Due(Booking booking, bool guestDataComplete, DateTime nowUtc) =>
        StayAlertSchedule.DueAlloggiatiStep(booking, guestDataComplete, Defaults, nowUtc);

    private static Booking Stay(
        DateTime? checkOut = null,
        DateTime? arrivedAt = null,
        BookingStatus status = BookingStatus.Confirmed) => new()
        {
            Id = Guid.NewGuid(),
            CheckInDate = CheckIn,
            CheckOutDate = checkOut ?? Utc(2026, 10, 8),
            ArrivedAt = arrivedAt,
            Status = status,
        };

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
