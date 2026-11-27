using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T01 (NF-52, CHK-12): when the alerts about a period and about the connection are due. The ISTAT period of October 2026 has its deadline on
/// 10 November (Europe/Rome on winter time, UTC+1): approaching three days before at 09:00, overdue the morning after the deadline day, then three reminders
/// every three days. Every threshold is an option.
/// </summary>
public class ComplianceAlertScheduleTests
{
    private static readonly ComplianceOptions Defaults = new();
    private static readonly Guid Property = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly DateOnly Deadline = new(2026, 11, 10);

    // ─── Period ───

    [Fact]
    public void DuePeriodStep_ThreeDaysBeforeTheDeadlineDayAt9Rome_IsTheDeadlineApproaching()
    {
        Assert.Null(Period(Utc(2026, 11, 7, 7, 59)));

        var step = Period(Utc(2026, 11, 7, 8, 0));
        Assert.NotNull(step);
        Assert.Equal(ComplianceAlertType.IstatPeriod, step.Type);
        Assert.Equal("2026-10", step.ReferenceKey);
        Assert.Equal(ComplianceAlertStage.DeadlineApproaching, step.Stage);
        Assert.Equal(ComplianceAlertKind.PeriodDeadlineApproaching, step.Alert.Kind);
        Assert.Equal(Property, step.Alert.PropertyId);
        Assert.Equal("2026-10", step.Alert.Period);
        Assert.Equal(Deadline, step.Alert.DeadlineDay);
        Assert.Equal(0, step.Alert.ReminderNumber);
    }

    [Fact]
    public void DuePeriodStep_TheWholeDeadlineDay_IsStillApproaching_AndTheMorningAfterIsOverdue()
    {
        // The deadline day ends at midnight of Rome (23:00 UTC): the cockpit turns red the day after, so the alert never says «overdue» before.
        Assert.Equal(ComplianceAlertStage.DeadlineApproaching, Period(Utc(2026, 11, 10, 22, 59))!.Stage);
        Assert.Equal(ComplianceAlertStage.DeadlineApproaching, Period(Utc(2026, 11, 11, 7, 59))!.Stage);

        var overdue = Period(Utc(2026, 11, 11, 8, 0));
        Assert.Equal(ComplianceAlertStage.Overdue, overdue!.Stage);
        Assert.Equal(ComplianceAlertKind.PeriodOverdue, overdue.Alert.Kind);
        Assert.Equal(0, overdue.Alert.ReminderNumber);
    }

    [Fact]
    public void DuePeriodStep_TheRemindersComeEveryThreeDays_UpToTheMaximum()
    {
        var first = Period(Utc(2026, 11, 14, 8, 0));
        var second = Period(Utc(2026, 11, 17, 8, 0));
        var third = Period(Utc(2026, 11, 20, 8, 0));

        Assert.Equal(ComplianceAlertStage.Overdue, Period(Utc(2026, 11, 14, 7, 59))!.Stage);
        Assert.Equal(ComplianceAlertStage.OverdueReminder(1), first!.Stage);
        Assert.Equal(1, first.Alert.ReminderNumber);
        Assert.Equal(3, first.Alert.MaxReminders);
        Assert.Equal(ComplianceAlertStage.OverdueReminder(2), second!.Stage);
        Assert.Equal(ComplianceAlertStage.OverdueReminder(3), third!.Stage);
        Assert.Equal(ComplianceAlertKind.PeriodOverdue, third.Alert.Kind);
        Assert.Equal([3, 4, 5], new[] { first.Stage, second.Stage, third.Stage });
    }

    [Fact]
    public void DuePeriodStep_ADayAfterTheLastReminder_ReturnsNothing_NoBacklog()
    {
        Assert.NotNull(Period(Utc(2026, 11, 21, 7, 59)));
        Assert.Null(Period(Utc(2026, 11, 21, 8, 0)));
        Assert.Null(Period(Utc(2026, 12, 20, 8, 0)));
    }

    [Fact]
    public void DuePeriodStep_WithNoReminders_TheOverdueIsTheLastStage()
    {
        var options = new ComplianceOptions { MaxPeriodReminders = 0 };

        Assert.Equal(ComplianceAlertStage.Overdue, ComplianceAlertSchedule.DuePeriodStep(
            ComplianceAlertType.IstatPeriod, Property, "2026-10", Deadline, options, Utc(2026, 11, 11, 8, 0))!.Stage);
        Assert.Null(ComplianceAlertSchedule.DuePeriodStep(
            ComplianceAlertType.IstatPeriod, Property, "2026-10", Deadline, options, Utc(2026, 11, 12, 8, 0)));
    }

    [Fact]
    public void DuePeriodStep_EveryThresholdIsAnOption()
    {
        var options = new ComplianceOptions
        {
            PeriodWarningDays = 5,
            MaxPeriodReminders = 1,
            PeriodReminderDays = 2,
            PeriodAlertHourLocal = 12,
        };

        Assert.Null(ComplianceAlertSchedule.DuePeriodStep(ComplianceAlertType.IstatPeriod, Property, "2026-10", Deadline, options, Utc(2026, 11, 5, 10, 59)));
        Assert.Equal(ComplianceAlertStage.DeadlineApproaching, ComplianceAlertSchedule.DuePeriodStep(
            ComplianceAlertType.IstatPeriod, Property, "2026-10", Deadline, options, Utc(2026, 11, 5, 11, 0))!.Stage);
        Assert.Equal(ComplianceAlertStage.OverdueReminder(1), ComplianceAlertSchedule.DuePeriodStep(
            ComplianceAlertType.IstatPeriod, Property, "2026-10", Deadline, options, Utc(2026, 11, 13, 11, 0))!.Stage);
    }

    [Fact]
    public void DuePeriodStep_TheTouristTaxPeriodHasTheSameStages_AndAConnectionIsNotAPeriod()
    {
        var step = ComplianceAlertSchedule.DuePeriodStep(
            ComplianceAlertType.TouristTaxPeriod, Property, "2026-10", Deadline, Defaults, Utc(2026, 11, 11, 8, 0));

        Assert.Equal(ComplianceAlertType.TouristTaxPeriod, step!.Type);
        Assert.Equal(ComplianceAlertStage.Overdue, step.Stage);
        Assert.Throws<ArgumentException>(() => ComplianceAlertSchedule.DuePeriodStep(
            ComplianceAlertType.ConnectionInactive, Property, "2026-10", Deadline, Defaults, Utc(2026, 11, 11, 8, 0)));
    }

    // ─── Connection ───

    [Fact]
    public void DueConnectionStep_AtDetection_IsTheFirstStage()
    {
        var step = ComplianceAlertSchedule.DueConnectionStep(Property, 0, null, Defaults, Utc(2026, 11, 3, 9, 0));

        Assert.NotNull(step);
        Assert.Equal(ComplianceAlertType.ConnectionInactive, step.Type);
        Assert.Equal(ComplianceAlertState.ConnectionReferenceKey, step.ReferenceKey);
        Assert.Equal(ComplianceAlertStage.ConnectionDetected, step.Stage);
        Assert.Equal(ComplianceAlertKind.ConnectionInactive, step.Alert.Kind);
        Assert.Equal(0, step.Alert.ReminderNumber);
        Assert.Null(step.Alert.Period);
    }

    [Fact]
    public void DueConnectionStep_TwoDaysAfterTheFirstAlert_IsTheSecondAndLast()
    {
        var first = Utc(2026, 11, 3, 9, 0);

        Assert.Null(ComplianceAlertSchedule.DueConnectionStep(Property, 1, first, Defaults, Utc(2026, 11, 5, 8, 59)));

        var step = ComplianceAlertSchedule.DueConnectionStep(Property, 1, first, Defaults, Utc(2026, 11, 5, 9, 0));
        Assert.Equal(ComplianceAlertStage.ConnectionReminder, step!.Stage);
        Assert.Equal(1, step.Alert.ReminderNumber);

        // Never a third, and never as a backlog.
        Assert.Null(ComplianceAlertSchedule.DueConnectionStep(Property, 2, first, Defaults, Utc(2026, 11, 8, 9, 0)));
        Assert.Null(ComplianceAlertSchedule.DueConnectionStep(Property, 1, first, Defaults, Utc(2026, 11, 6, 9, 0)));
    }

    [Fact]
    public void DueConnectionStep_TheReminderDelayIsAnOption_AndNeedsTheTimeOfTheFirstAlert()
    {
        var options = new ComplianceOptions { ConnectionReminderDays = 4 };
        var first = Utc(2026, 11, 3, 9, 0);

        Assert.Null(ComplianceAlertSchedule.DueConnectionStep(Property, 1, first, options, Utc(2026, 11, 6, 9, 0)));
        Assert.NotNull(ComplianceAlertSchedule.DueConnectionStep(Property, 1, first, options, Utc(2026, 11, 7, 9, 0)));
        Assert.Null(ComplianceAlertSchedule.DueConnectionStep(Property, 1, null, options, Utc(2026, 11, 20, 9, 0)));
    }

    // ─── The popup rule ───

    [Fact]
    public void IsLegalDeadlineNear_IsWithinTwentyFourHoursBeforeOrAfter()
    {
        var deadline = ComplianceAlertSchedule.DeadlineEndUtc(Deadline); // 11 November 00:00 Rome = 10 November 23:00 UTC

        Assert.Equal(Utc(2026, 11, 10, 23, 0), deadline);
        Assert.True(ComplianceAlertSchedule.IsLegalDeadlineNear(deadline, Utc(2026, 11, 9, 23, 0)));
        Assert.False(ComplianceAlertSchedule.IsLegalDeadlineNear(deadline, Utc(2026, 11, 9, 22, 59)));
        Assert.True(ComplianceAlertSchedule.IsLegalDeadlineNear(deadline, Utc(2026, 11, 11, 23, 0)));
        Assert.False(ComplianceAlertSchedule.IsLegalDeadlineNear(deadline, Utc(2026, 11, 11, 23, 1)));
        Assert.False(ComplianceAlertSchedule.IsLegalDeadlineNear(null, Utc(2026, 11, 10, 12, 0)));
    }

    private static ComplianceAlertStep? Period(DateTime nowUtc) =>
        ComplianceAlertSchedule.DuePeriodStep(ComplianceAlertType.IstatPeriod, Property, "2026-10", Deadline, Defaults, nowUtc);

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
