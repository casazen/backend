using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E24/F01/US01/T02 (NF-120, NF-21): how the routine engine runs <c>CreateCleaningRequest</c> (what the automation answers becomes what the log of the routine says), how the consumer
/// of the changes of a stay hands the events to the automation, and the time and the length of the cleaning (<see cref="CleaningSchedule"/>).
/// </summary>
public class CleaningAtCheckoutRoutineTests
{
    private static readonly Guid OrgA = RoutineRig.OrgA;
    private static readonly string CleaningAction = RoutineRig.Actions(("CreateCleaningRequest", "{}"));

    // ─── The action in the engine ────────────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<CleaningAutomationOutcome, string?, string, string?> Answers => new()
    {
        { CleaningAutomationOutcome.Created, null, RoutineActionOutcomes.Done, null },
        { CleaningAutomationOutcome.AlreadyCreated, null, RoutineActionOutcomes.Done, null },
        { CleaningAutomationOutcome.AlreadyPlanned, null, RoutineActionOutcomes.Done, null },
        { CleaningAutomationOutcome.NotConfigured, null, RoutineActionOutcomes.Blocked, RoutineCodes.CleaningNotConfigured },
        { CleaningAutomationOutcome.StayNotActive, null, RoutineActionOutcomes.Blocked, RoutineCodes.BookingNotActive },
        { CleaningAutomationOutcome.Failed, ServiceRequestErrorCodes.SupplierInactive, RoutineActionOutcomes.Failed, ServiceRequestErrorCodes.SupplierInactive },
        { CleaningAutomationOutcome.Failed, CleaningAutomationCodes.StayNotFound, RoutineActionOutcomes.Failed, RoutineCodes.CleaningNeedsBooking },
        { CleaningAutomationOutcome.ModuleOff, null, RoutineActionOutcomes.Failed, RoutineCodes.ActionError },
    };

    [Theory]
    [InlineData("YES", true, RoutineActionOutcomes.Done, null)]
    [InlineData("NO", true, RoutineActionOutcomes.Skipped, RoutineCodes.AiConditionFalse)]
    [InlineData("UNKNOWN", true, RoutineActionOutcomes.Blocked, RoutineCodes.AiFactsMissing)]
    [InlineData("YES", false, RoutineActionOutcomes.Failed, RoutineCodes.AiUnavailable)]
    public async Task AgentEvaluatesOnceAndDispatchesOnlyTheConfiguredOperation(
        string answer, bool configured, string outcome, string? code)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(answer, 1, 1, AiModelTier.Economy, false, configured));
        ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.Is<string>(k => k.StartsWith("agent-plan:")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult("[0]", 1, 1, AiModelTier.Economy, false));
        rig.Ai = ai.Object;
        var cleaning = new Mock<ICleaningAutomationService>(MockBehavior.Strict);
        cleaning.Setup(c => c.CreateForStayAsync(OrgA, booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleaningAutomationResult(CleaningAutomationOutcome.Created, null));
        rig.Cleaning = cleaning.Object;
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckout,
            RoutineRig.Actions(("CreateCleaningRequest", """{"aiCondition":"Only for stays of at least three nights"}""")));

        await rig.TriggerAsync(OrgA, RoutineEventType.AfterCheckout, RoutineRig.PayloadOf(booking), "checkout");
        await rig.TriggerAsync(OrgA, RoutineEventType.AfterCheckout, RoutineRig.PayloadOf(booking), "checkout");

        var result = Assert.Single(RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Actions);
        Assert.Equal((outcome, code), (result.Outcome, result.Code));
        Assert.Equal(2, ai.Invocations.Count);
        Assert.Equal(outcome == RoutineActionOutcomes.Done ? 1 : 0, cleaning.Invocations.Count);
        await using var db = rig.NewDb();
        Assert.Empty(db.InternalActivities);
    }

    /// <summary>What the automation answers is what the log says: done for a cleaning made or already there, blocked for a property with no cleaner or a stay that does not happen, failed with the code of the rule.</summary>
    [Theory]
    [MemberData(nameof(Answers))]
    public async Task TheAnswerOfTheAutomation_IsTheResultOfTheAction(CleaningAutomationOutcome outcome, string? code, string expectedOutcome, string? expectedCode)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        var cleaning = new Mock<ICleaningAutomationService>(MockBehavior.Strict);
        cleaning
            .Setup(c => c.CreateForStayAsync(OrgA, booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleaningAutomationResult(outcome, code));
        rig.Cleaning = cleaning.Object;
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckout, CleaningAction);

        await rig.TriggerAsync(OrgA, RoutineEventType.AfterCheckout, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        var action = Assert.Single(RoutineRig.ResultOf(execution).Actions);
        Assert.Equal(new RoutineActionResult(0, RoutineActionType.CreateCleaningRequest, expectedOutcome, expectedCode), action);
        Assert.Equal(expectedOutcome == RoutineActionOutcomes.Failed ? RoutineExecutionOutcomes.Failed : RoutineExecutionOutcomes.Success, execution.Outcome);
        cleaning.Verify(c => c.CreateForStayAsync(OrgA, booking.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The automation is asked for the org of the routine, never another: the booking of another org's event is not its business.</summary>
    [Fact]
    public async Task TheAutomation_IsAskedInTheOrgOfTheRoutine()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        var cleaning = new Mock<ICleaningAutomationService>(MockBehavior.Strict);
        cleaning
            .Setup(c => c.CreateForStayAsync(RoutineRig.OrgB, booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleaningAutomationResult(CleaningAutomationOutcome.Failed, CleaningAutomationCodes.StayNotFound));
        rig.Cleaning = cleaning.Object;
        await rig.SeedRoutineAsync(RoutineRig.OrgB, RoutineEventType.AfterCheckout, CleaningAction);

        await rig.TriggerAsync(RoutineRig.OrgB, RoutineEventType.AfterCheckout, RoutineRig.PayloadOf(booking));

        var action = Assert.Single(RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Actions);
        Assert.Equal((RoutineActionOutcomes.Failed, RoutineCodes.CleaningNeedsBooking), (action.Outcome, action.Code));
    }

    [Fact]
    public async Task AnEventThatNamesNoBooking_FailsTheAction_AndTheAutomationIsNeverAsked()
    {
        var rig = new RoutineRig();
        rig.Cleaning = new Mock<ICleaningAutomationService>(MockBehavior.Strict).Object;
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckout, CleaningAction);

        await rig.TriggerAsync(OrgA, RoutineEventType.AfterCheckout, new { PropertyId = Guid.NewGuid() });

        var action = Assert.Single(RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Actions);
        Assert.Equal((RoutineActionOutcomes.Failed, RoutineCodes.CleaningNeedsBooking), (action.Outcome, action.Code));
    }

    /// <summary>A routine in test mode simulates the action (nothing is created), and an engine with no automation never pretends to have made a cleaning.</summary>
    [Fact]
    public async Task TestMode_Simulates_AndAnEngineWithNoAutomation_FailsTheAction()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        var strict = new Mock<ICleaningAutomationService>(MockBehavior.Strict);
        rig.Cleaning = strict.Object;
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckout, CleaningAction, testMode: true);

        await rig.TriggerAsync(OrgA, RoutineEventType.AfterCheckout, RoutineRig.PayloadOf(booking));
        var simulated = Assert.Single(RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Actions);

        var other = new RoutineRig();
        var otherBooking = await other.SeedBookingAsync(OrgA);
        await other.SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckout, CleaningAction);
        await other.TriggerAsync(OrgA, RoutineEventType.AfterCheckout, RoutineRig.PayloadOf(otherBooking));
        var unavailable = Assert.Single(RoutineRig.ResultOf(await other.SingleExecutionAsync()).Actions);

        Assert.Equal(RoutineActionOutcomes.Simulated, simulated.Outcome);
        Assert.Equal((RoutineActionOutcomes.Failed, RoutineCodes.ActionNotAvailable), (unavailable.Outcome, unavailable.Code));
    }

    /// <summary>The ready routine of the brief (the time of the check-out, no offset, the action) is a routine the API saves, and it can be switched on: its action is available.</summary>
    [Fact]
    public async Task TheReadyRoutine_IsARoutineTheApiSaves_AndItCanBeActivated()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var management = rig.Management(db);

        var created = await management.CreateAsync(
            OrgA,
            new RoutineCommand(
                "Pulizia al check-out",
                nameof(RoutineEventType.AfterCheckout),
                RoutineRig.Json("""{"hours":0}"""),
                null,
                [new RoutineActionInput(nameof(RoutineActionType.CreateCleaningRequest), RoutineRig.Json("{}"))],
                TestMode: false,
                Active: false));
        var active = await management.SetActiveAsync(OrgA, created.Id, active: true);

        Assert.True(active.Active);
        Assert.Equal(RoutineEventType.AfterCheckout, active.EventType);
        Assert.Equal(0, TimeTrigger.OffsetHours(active.EventConfig));
    }

    // ─── The consumer of the changes of a stay ───────────────────────────────────────────────────────────────────────────────

    private static DomainEvent Event(DomainEventType type, Guid orgId, Guid entityId) =>
        new(Guid.NewGuid(), orgId, type, entityId, new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc), "{}");

    [Fact]
    public async Task TheDatesOfTheStayChange_TheAutomationFollowsThem()
    {
        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var cleaning = new Mock<ICleaningAutomationService>(MockBehavior.Strict);
        cleaning
            .Setup(c => c.FollowStayDatesAsync(orgId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleaningAutomationResult(CleaningAutomationOutcome.Moved));

        await new CleaningBookingChangesHandler(cleaning.Object, NullLogger<CleaningBookingChangesHandler>.Instance)
            .HandleAsync(Event(DomainEventType.BookingModified, orgId, bookingId), CancellationToken.None);

        cleaning.Verify(c => c.FollowStayDatesAsync(orgId, bookingId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheBookingIsCancelled_TheAutomationFollowsIt()
    {
        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var cleaning = new Mock<ICleaningAutomationService>(MockBehavior.Strict);
        cleaning
            .Setup(c => c.FollowStayCancellationAsync(orgId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleaningAutomationResult(CleaningAutomationOutcome.Cancelled));

        await new CleaningBookingChangesHandler(cleaning.Object, NullLogger<CleaningBookingChangesHandler>.Instance)
            .HandleAsync(Event(DomainEventType.BookingCancelled, orgId, bookingId), CancellationToken.None);

        cleaning.Verify(c => c.FollowStayCancellationAsync(orgId, bookingId, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A stay marked as a no-show does not happen: the automation follows it as it follows a cancellation (it reads the stay and cancels the cleaning if not started).</summary>
    [Fact]
    public async Task TheStayIsMarkedAsANoShow_TheAutomationFollowsItLikeACancellation()
    {
        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var cleaning = new Mock<ICleaningAutomationService>(MockBehavior.Strict);
        cleaning
            .Setup(c => c.FollowStayCancellationAsync(orgId, bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleaningAutomationResult(CleaningAutomationOutcome.Cancelled));

        await new CleaningBookingChangesHandler(cleaning.Object, NullLogger<CleaningBookingChangesHandler>.Instance)
            .HandleAsync(Event(DomainEventType.BookingNoShowMarked, orgId, bookingId), CancellationToken.None);

        cleaning.Verify(c => c.FollowStayCancellationAsync(orgId, bookingId, It.IsAny<CancellationToken>()), Times.Once);
        cleaning.VerifyNoOtherCalls();
    }

    /// <summary>Every other event is not the handler's, and an event that names no booking is ignored.</summary>
    [Fact]
    public async Task EveryOtherEvent_AndAnEventWithNoBooking_IsIgnored()
    {
        var strict = new Mock<ICleaningAutomationService>(MockBehavior.Strict);
        var handler = new CleaningBookingChangesHandler(strict.Object, NullLogger<CleaningBookingChangesHandler>.Instance);

        foreach (var type in Enum.GetValues<DomainEventType>().Where(t => t is not (DomainEventType.BookingModified or DomainEventType.BookingCancelled or DomainEventType.BookingNoShowMarked)))
            await handler.HandleAsync(Event(type, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        await handler.HandleAsync(Event(DomainEventType.BookingModified, Guid.NewGuid(), Guid.Empty), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.BookingCancelled, Guid.NewGuid(), Guid.Empty), CancellationToken.None);
        await handler.HandleAsync(Event(DomainEventType.BookingNoShowMarked, Guid.NewGuid(), Guid.Empty), CancellationToken.None);

        strict.VerifyNoOtherCalls();
    }

    // ─── When and for how long ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly DateTime CheckOutDay = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The time of the setup wins, then the check-out time of the property, then 10:00; the day is the check-out day on the clock of the property, summer time or not.</summary>
    [Fact]
    public void StartUtc_IsTheCheckOutDayAtTheTimeOfTheSetup_ThenOfTheProperty_ThenTen()
    {
        var setup = CleaningSchedule.StartUtc(CheckOutDay, new TimeOnly(12, 15), new TimeOnly(11, 0), "Europe/Rome");
        var property = CleaningSchedule.StartUtc(CheckOutDay, null, new TimeOnly(11, 0), "Europe/Rome");
        var fallback = CleaningSchedule.StartUtc(CheckOutDay, null, null, "Europe/Rome");
        var winter = CleaningSchedule.StartUtc(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), null, null, "Europe/Rome");

        Assert.Equal(new DateTime(2026, 10, 12, 10, 15, 0, DateTimeKind.Utc), setup);
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc), property);
        Assert.Equal(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc), fallback);
        Assert.Equal(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc), winter);
        Assert.All([setup, property, fallback, winter], instant => Assert.Equal(DateTimeKind.Utc, instant.Kind));
    }

    /// <summary>The clock is the one of the property, and Rome's for a property whose zone is missing or unknown.</summary>
    [Fact]
    public void StartUtc_ReadsTheClockOfTheProperty()
    {
        var london = CleaningSchedule.StartUtc(CheckOutDay, null, null, "Europe/London");
        var none = CleaningSchedule.StartUtc(CheckOutDay, null, null, null);
        var unknown = CleaningSchedule.StartUtc(CheckOutDay, null, null, "Mars/Olympus_Mons");

        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc), london);
        Assert.Equal(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc), none);
        Assert.Equal(none, unknown);
    }

    [Fact]
    public void Duration_IsTheOneOfTheSetup_OrTwoHours()
    {
        Assert.Equal(90, CleaningSchedule.DurationOf(90));
        Assert.Equal(120, CleaningSchedule.DurationOf(null));
        Assert.Equal(120, CleaningSchedule.DefaultDurationMinutes);
    }
}
