using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Scheduling;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Events;
using Xunit;

namespace Casazen.Tests.Unit.Scheduling;

/// <summary>
/// E05/F07/US01/T02 (NF-71): the planner of the scheduled messages is pure and agrees with the time triggers of the routines (the moment the routine fires is the moment the host sees), it is right across
/// the two days a year the clock changes, it never plans outside the send window, and it plans nothing for what a person has not activated.
/// </summary>
public class ScheduledMessagePlannerTests
{
    private static readonly string Template = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f").ToString();

    private static readonly ScheduledMessageOptions Options = new();

    private static PlanningRoutine Routine(
        RoutineEventType eventType,
        string eventConfig = "{}",
        string? actions = null,
        bool active = true,
        bool testMode = false) =>
        new(
            Guid.NewGuid(),
            "Messaggio (TEST)",
            eventType,
            eventConfig,
            actions ?? $$$"""[{"type":"MessageToGuest","config":{"templateId":"{{{Template}}}"}}]""",
            active,
            testMode);

    private static PlanningBooking Stay(
        DateOnly checkIn,
        DateOnly checkOut,
        TimeOnly? checkInFrom = null,
        TimeOnly? checkOutBy = null,
        string? timezone = "Europe/Rome",
        BookingStatus status = BookingStatus.Confirmed,
        bool noShow = false,
        DateTime? createdAt = null,
        DateTime? confirmedAt = null) =>
        new(
            Guid.NewGuid(),
            status,
            noShow,
            checkIn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            checkOut.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            createdAt ?? new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
            confirmedAt,
            checkInFrom,
            checkOutBy,
            timezone);

    [Fact]
    public void BeforeCheckin_IsTheMomentTheTimeTriggerFiresAt_TheTimeOfTheProperty_MinusTheOffset()
    {
        // 20 November 2026, check-in from 15:00 Rome (UTC+1): 14:00 UTC, two days before = 18 November 14:00 UTC.
        var planned = Assert.Single(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, """{"days":2}"""),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0)),
            Options));

        Assert.Equal(0, planned.ActionIndex);
        Assert.Equal(Template, planned.TemplateRef);
        Assert.Equal(new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc), planned.PlannedAtUtc);
    }

    [Fact]
    public void AfterCheckout_AddsTheHoursToTheCheckOutTimeOfTheProperty()
    {
        // Check-out by 10:00 Rome (UTC+1) on 23 November = 09:00 UTC, three hours after = 12:00 UTC.
        var planned = Assert.Single(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.AfterCheckout, """{"hours":3}"""),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkOutBy: new TimeOnly(10, 0)),
            Options));

        Assert.Equal(new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc), planned.PlannedAtUtc);
    }

    [Theory]
    [InlineData("""{"days":2}""", 48)]
    [InlineData("""{"hours":48}""", 48)]
    [InlineData("""{"days":1,"hours":5}""", 29)]
    [InlineData("{}", 0)]
    [InlineData("", 0)]
    [InlineData("""{"days":-1}""", null)]
    [InlineData("""{"days":"two"}""", null)]
    [InlineData("""{"days":367}""", null)]
    [InlineData("[]", null)]
    public void TheOffsetIsReadAsTheTimeTriggerReadsIt(string eventConfig, int? expected)
    {
        Assert.Equal(expected, ScheduledMessagePlanner.OffsetHours(eventConfig));
        Assert.Equal(TimeTrigger.OffsetHours(eventConfig), ScheduledMessagePlanner.OffsetHours(eventConfig));
    }

    /// <summary>
    /// PROOF TEST: the timezone rule (always UTC internally). Across the two days a year the clock changes the planned instant is the wall-clock time of the property on that date, plus the offset in whole hours,
    /// exactly as <c>TimeEventEmitterJob</c> computes the moment the routine fires: 15:00 Rome is 14:00 UTC in winter and 13:00 UTC in summer, and a time that does not exist (02:30 on 29 March 2026) or happens
    /// twice (02:30 on 25 October 2026) has the answer <c>RomeCalendar</c> gives it.
    /// </summary>
    [Theory]
    [InlineData(2026, 3, 29, 15, 0, 13)] // the day summer time starts: 15:00 is already UTC+2
    [InlineData(2026, 3, 28, 15, 0, 14)] // the day before: UTC+1
    [InlineData(2026, 10, 25, 15, 0, 14)] // the day summer time ends: 15:00 is UTC+1 again
    [InlineData(2026, 10, 24, 15, 0, 13)] // the day before: UTC+2
    public void BeforeCheckin_AcrossTheDaylightSavingChanges_IsTheWallClockTimeOfTheProperty(int year, int month, int day, int hour, int minute, int expectedUtcHour)
    {
        var date = new DateOnly(year, month, day);
        var routine = Routine(RoutineEventType.BeforeCheckin, "{}");
        var planned = Assert.Single(ScheduledMessagePlanner.Plan(routine, Stay(date, date.AddDays(2), checkInFrom: new TimeOnly(hour, minute)), Options));

        Assert.Equal(new DateTime(year, month, day, expectedUtcHour, minute, 0, DateTimeKind.Utc), planned.PlannedAtUtc);
        Assert.Equal(RomeCalendar.ToUtc(date, new TimeOnly(hour, minute)), planned.PlannedAtUtc);
    }

    [Fact]
    public void ThePlanIsOnTheClockOfThePropertyNotOfRome()
    {
        // A property in Lisbon (UTC+0 in winter): 15:00 there is 15:00 UTC.
        var planned = Assert.Single(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, "{}"),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0), timezone: "Europe/Lisbon"),
            Options));

        Assert.Equal(new DateTime(2026, 11, 20, 15, 0, 0, DateTimeKind.Utc), planned.PlannedAtUtc);
    }

    [Fact]
    public void AMomentBeforeTheWindowWaitsForIt_AndOneAfterItWaitsForTheNextMorning()
    {
        // 03:00 Rome on the day of the check-in (no time on the property, 02:00 after midnight): before the window, so 08:00 the same day (07:00 UTC).
        var early = Assert.Single(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, """{}"""),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(3, 0)),
            Options));
        Assert.Equal(new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc), early.PlannedAtUtc);

        // 23:00 Rome the evening before (check-in 15:00, 16 hours before): after the window, so 08:00 the next morning, which is the day of the check-in.
        var late = Assert.Single(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, """{"hours":16}"""),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0)),
            Options));
        Assert.Equal(new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc), late.PlannedAtUtc);
    }

    [Fact]
    public void TheWindowIsTheOneOfTheOptions_AndAnUpsideDownOneIsBroughtBackToTheDefault()
    {
        var tight = new ScheduledMessageOptions { SendWindowFrom = new TimeOnly(10, 0), SendWindowTo = new TimeOnly(12, 0) };
        var planned = Assert.Single(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, "{}"),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0)),
            tight));
        // 15:00 Rome is after the window of 10-12: the next morning at 10:00 (09:00 UTC).
        Assert.Equal(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc), planned.PlannedAtUtc);

        var upsideDown = new ScheduledMessageOptions { SendWindowFrom = new TimeOnly(22, 0), SendWindowTo = new TimeOnly(6, 0) };
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(21, 0)), upsideDown.EffectiveSendWindow);
    }

    [Fact]
    public void EveryWaitBeforeTheMessageAddsItsDelay_AndAWaitTheEngineWouldRefuseEndsThePlan()
    {
        var actions = $$$"""
            [{"type":"Wait","config":{"days":1}},
             {"type":"MessageToGuest","config":{"templateId":"{{{Template}}}"}},
             {"type":"Wait","config":{"hours":2}},
             {"type":"MessageToGuest","config":{"templateId":"{{{Template}}}"}}]
            """;
        var plan = ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, """{"days":3}""", actions),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0)),
            Options);

        Assert.Equal([1, 3], plan.Select(p => p.ActionIndex));
        Assert.Equal(new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc), plan[0].PlannedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 18, 16, 0, 0, DateTimeKind.Utc), plan[1].PlannedAtUtc);

        var broken = $$$"""[{"type":"Wait","config":{"days":0}},{"type":"MessageToGuest","config":{"templateId":"{{{Template}}}"}}]""";
        Assert.Empty(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, "{}", broken),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0)),
            Options));
    }

    [Fact]
    public void BookingNewAndBookingConfirmed_AreTheMomentOfTheEvent_ClampedIntoTheWindow()
    {
        var confirmed = new DateTime(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);
        var booking = Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), createdAt: new DateTime(2026, 11, 12, 2, 0, 0, DateTimeKind.Utc), confirmedAt: confirmed);

        Assert.Equal(confirmed, Assert.Single(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BookingConfirmed), booking, Options)).PlannedAtUtc);

        // 02:00 UTC is 03:00 Rome: before the window, so 08:00 Rome (07:00 UTC) the same day.
        Assert.Equal(new DateTime(2026, 11, 12, 7, 0, 0, DateTimeKind.Utc), Assert.Single(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BookingNew), booking, Options)).PlannedAtUtc);
    }

    [Theory]
    [InlineData(RoutineEventType.PaymentSucceeded)]
    [InlineData(RoutineEventType.GuestMessageReceived)]
    [InlineData(RoutineEventType.ActivityCompleted)]
    [InlineData(RoutineEventType.WeeklyRecurrence)]
    [InlineData(RoutineEventType.BookingCanceled)]
    public void AnEventThatCannotBeForeseenPlansNothing(RoutineEventType eventType)
    {
        Assert.Empty(ScheduledMessagePlanner.Plan(
            Routine(eventType),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0)),
            Options));
    }

    [Fact]
    public void AnInactiveRoutine_ARoutineInTestMode_AndAStayThatDoesNotHappen_PlanNothing()
    {
        var booking = Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0));

        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, active: false), booking, Options));
        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, testMode: true), booking, Options));
        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin), booking with { Status = BookingStatus.Cancelled }, Options));
        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin), booking with { Status = BookingStatus.Pending }, Options));
        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin), booking with { NoShow = true }, Options));
        Assert.Single(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin), booking with { Status = BookingStatus.CheckedOut }, Options));
    }

    /// <summary>
    /// A stay that happens is the one definition of an active stay (<c>AlloggiatiStatusRules.ActiveBookingStatuses</c>, no no-show) for every status there is, so a status added later to it
    /// is planned and one that is not in it never is: the planner keeps no list of its own.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheStaysThatGetAMessage_AreTheOnesTheOneDefinitionOfAnActiveStayHolds_ForEveryStatus(bool noShow)
    {
        foreach (var status in Enum.GetValues<BookingStatus>())
        {
            var booking = Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0), status: status, noShow: noShow);

            var planned = ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin), booking, Options);

            Assert.True(
                (planned.Count == 1) == (!noShow && Casazen.Core.Regulatory.AlloggiatiStatusRules.ActiveBookingStatuses.Contains(status)),
                $"{status} (no-show: {noShow}) is planned differently from the one definition of an active stay");
        }
    }

    [Fact]
    public void AnActionThatIsNotAMessageWithATemplate_IsNotPlanned_AndABadOffsetPlansNothing()
    {
        var actions = """[{"type":"CreateActivity","config":{"text":"x"}},{"type":"MessageToGuest","config":{}},{"type":"MessageToGuest","config":{"templateId":"not-a-guid"}}]""";
        var booking = Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0));

        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, "{}", actions), booking, Options));
        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, """{"days":-3}"""), booking, Options));
        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, "{}", "not json"), booking, Options));
    }

    [Fact]
    public void IsInsideWindow_JudgesTheClockOfTheProperty()
    {
        var rome = RomeCalendar.ZoneOf("Europe/Rome");
        var from = new TimeOnly(8, 0);
        var to = new TimeOnly(21, 0);

        Assert.True(ScheduledMessagePlanner.IsInsideWindow(new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc), rome, from, to)); // 08:00 Rome
        Assert.True(ScheduledMessagePlanner.IsInsideWindow(new DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc), rome, from, to)); // 21:00 Rome
        Assert.False(ScheduledMessagePlanner.IsInsideWindow(new DateTime(2026, 11, 20, 6, 59, 0, DateTimeKind.Utc), rome, from, to)); // 07:59 Rome
        Assert.False(ScheduledMessagePlanner.IsInsideWindow(new DateTime(2026, 11, 20, 22, 0, 0, DateTimeKind.Utc), rome, from, to)); // 23:00 Rome
    }
}
