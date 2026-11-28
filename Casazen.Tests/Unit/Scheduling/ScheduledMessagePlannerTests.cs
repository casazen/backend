using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Scheduling;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Events;
using Xunit;

namespace Casazen.Tests.Unit.Scheduling;

public class ScheduledMessagePlannerTests
{
    private static readonly string Template = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f").ToString();

    private static readonly ScheduledMessageOptions Options = new();

    private static PlanningRoutine Routine(
        RoutineEventType eventType,
        string eventConfig = "{}",
        string? actions = null,
        bool active = true,
        bool testMode = false,
        string? agentObjective = null) =>
        new(
            Guid.NewGuid(),
            "Messaggio (TEST)",
            eventType,
            eventConfig,
            actions ?? $$$"""[{"type":"MessageToGuest","config":{"templateId":"{{{Template}}}"}}]""",
            active,
            testMode,
            agentObjective);

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
    [InlineData(2026, 3, 29, 15, 0, 13)]
    [InlineData(2026, 3, 28, 15, 0, 14)]
    [InlineData(2026, 10, 25, 15, 0, 14)]
    [InlineData(2026, 10, 24, 15, 0, 13)]
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
        var planned = Assert.Single(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, "{}"),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0), timezone: "Europe/Lisbon"),
            Options));

        Assert.Equal(new DateTime(2026, 11, 20, 15, 0, 0, DateTimeKind.Utc), planned.PlannedAtUtc);
    }

    [Fact]
    public void AMomentBeforeTheWindowWaitsForIt_AndOneAfterItWaitsForTheNextMorning()
    {
        var early = Assert.Single(ScheduledMessagePlanner.Plan(
            Routine(RoutineEventType.BeforeCheckin, """{}"""),
            Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(3, 0)),
            Options));
        Assert.Equal(new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc), early.PlannedAtUtc);

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

        Assert.True(ScheduledMessagePlanner.IsInsideWindow(new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc), rome, from, to));
        Assert.True(ScheduledMessagePlanner.IsInsideWindow(new DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc), rome, from, to));
        Assert.False(ScheduledMessagePlanner.IsInsideWindow(new DateTime(2026, 11, 20, 6, 59, 0, DateTimeKind.Utc), rome, from, to));
        Assert.False(ScheduledMessagePlanner.IsInsideWindow(new DateTime(2026, 11, 20, 22, 0, 0, DateTimeKind.Utc), rome, from, to));
    }

    [Fact]
    public void AnActionWithAnAiCondition_IsNotPlanned_AndARoutineWithAnAgentObjective_PlansNothing()
    {
        var actions = $$$"""[{"type":"MessageToGuest","config":{"templateId":"{{{Template}}}","aiCondition":"only for stays of four nights or more"}},{"type":"MessageToGuest","config":{"templateId":"{{{Template}}}"}}]""";
        var booking = Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0));

        var planned = Assert.Single(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, """{"days":1}""", actions), booking, Options));
        Assert.Equal(1, planned.ActionIndex);

        Assert.Empty(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, """{"days":1}""", agentObjective: "Only for guests who come from abroad"), booking, Options));
        Assert.Single(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, """{"days":1}""", agentObjective: "   "), booking, Options));
    }

    [Theory]
    [InlineData("""{"templateId":"__T__","isCampaign":true}""", true)]
    [InlineData("""{"templateId":"__T__","isCampaign":"false"}""", true)]
    [InlineData("""{"templateId":"__T__","isCampaign":0}""", true)]
    [InlineData("""{"templateId":"__T__","isCampaign":null}""", true)]
    [InlineData("""{"templateId":"__T__","isCampaign":false}""", false)]
    [InlineData("""{"templateId":"__T__"}""", false)]
    public void ACampaign_IsMarkedOnThePlannedMessage_AsTheEngineReadsTheMarker(string config, bool expected)
    {
        var actions = "[{\"type\":\"MessageToGuest\",\"config\":" + config.Replace("__T__", Template) + "}]";
        var booking = Stay(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), checkInFrom: new TimeOnly(15, 0));

        var planned = Assert.Single(ScheduledMessagePlanner.Plan(Routine(RoutineEventType.BeforeCheckin, """{"days":1}""", actions), booking, Options));

        Assert.Equal(expected, planned.IsCampaign);
    }

    [Fact]
    public void TheMessagesTheEngineDecides_AreEngineOwned_AndTheOnesThePlanWritesAreNot()
    {
        var withCondition = $$$"""[{"type":"MessageToGuest","config":{"templateId":"{{{Template}}}","aiCondition":"x"}}]""";

        Assert.True(ScheduledMessagePlanner.IsEngineOwned(Routine(RoutineEventType.PaymentSucceeded), 0));
        Assert.True(ScheduledMessagePlanner.IsEngineOwned(Routine(RoutineEventType.BeforeCheckin, """{"days":1}""", agentObjective: "Only for guests who come from abroad"), 0));
        Assert.True(ScheduledMessagePlanner.IsEngineOwned(Routine(RoutineEventType.BeforeCheckin, """{"days":1}""", withCondition), 0));
        Assert.False(ScheduledMessagePlanner.IsEngineOwned(Routine(RoutineEventType.BeforeCheckin, """{"days":1}"""), 0));

        Assert.False(ScheduledMessagePlanner.IsEngineOwned(Routine(RoutineEventType.PaymentSucceeded, active: false), 0));
        Assert.False(ScheduledMessagePlanner.IsEngineOwned(Routine(RoutineEventType.PaymentSucceeded, testMode: true), 0));
        Assert.False(ScheduledMessagePlanner.IsEngineOwned(Routine(RoutineEventType.PaymentSucceeded), 3));
        Assert.False(ScheduledMessagePlanner.IsEngineOwned(Routine(RoutineEventType.PaymentSucceeded, actions: """[{"type":"CreateActivity","config":{"text":"x"}}]"""), 0));
    }
}
