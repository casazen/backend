using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Infrastructure.Jobs;
using Hangfire.Common;
using Hangfire.States;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E01/F04/US01/T01 (NF-21, ADR-022): the routine engine does real work. A condition is evaluated on the booking of the event and decides whether a
/// routine runs; a Wait schedules the job that resumes it and the resumed execution continues from where it stopped, with what the event said, once;
/// an action with no implementation fails with a code and never pretends; and every execution says, action by action, what happened.
/// </summary>
public class RoutineEngineTests
{
    private static readonly Guid OrgA = RoutineRig.OrgA;
    private static readonly Guid OrgB = RoutineRig.OrgB;

    /// <summary>The condition the editor writes for "the channel is X".</summary>
    private static string ChannelIs(string channel) =>
        $$"""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"channel","operator":"equals","value":"{{channel}}"}]}]}""";

    private static string Rule(string fieldId, string op, string valueJson, string groupOperator = "AND") =>
        $$"""{"version":"1.0","groups":[{"operator":"{{groupOperator}}","rules":[{"fieldId":"{{fieldId}}","operator":"{{op}}","value":{{valueJson}}}]}]}""";

    private static readonly string WaitAnHour = RoutineRig.Actions(("Wait", """{"hours":1}"""));

    // ─── Conditions (AC-NF21-2) ─────────────────────────────────────────────────

    /// <summary>
    /// Proof (data rule): a routine whose condition is not met performs no action. Before this task every condition was met, so a routine meant for the
    /// Airbnb stays also ran for the direct ones. Nothing is recorded either: the event is not about this routine.
    /// </summary>
    [Fact]
    public async Task RoutineEngine_UnmetCondition_RunsNoAction_Test()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Direct);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, WaitAnHour, ChannelIs("Airbnb"));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        Assert.Empty(await rig.ExecutionsAsync());
        Assert.Empty(rig.Scheduled);
    }

    [Fact]
    public async Task Trigger_ConditionMet_RunsTheRoutine()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, WaitAnHour, ChannelIs("Airbnb"));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Waiting, execution.Outcome);
        Assert.Single(rig.Scheduled);
    }

    [Fact]
    public async Task Trigger_NoCondition_AlwaysRuns_EvenForAnEventWithNoBooking()
    {
        var rig = new RoutineRig();
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", conditionJson: "{}");
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", conditionJson: string.Empty);

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, new { Nothing = "to see" });

        var executions = await rig.ExecutionsAsync();
        Assert.Equal(2, executions.Count);
        Assert.All(executions, e => Assert.Equal(RoutineExecutionOutcomes.Success, e.Outcome));
    }

    /// <summary>The rules of the catalog the routines read, evaluated on a booking of Airbnb, 3 nights, 2 guests, made 10 days before its check-in.</summary>
    [Theory]
    [InlineData("channel", "equals", "\"airbnb\"", "AND", true)]
    [InlineData("channel", "notEquals", "\"Airbnb\"", "AND", false)]
    [InlineData("channel", "in", "[\"Direct\",\"Airbnb\"]", "AND", true)]
    [InlineData("channel", "notIn", "[\"Direct\",\"Airbnb\"]", "AND", false)]
    [InlineData("channel", "in", "[]", "AND", false)]
    [InlineData("channel", "notIn", "[]", "AND", false)]
    [InlineData("stayDuration", "equals", "3", "AND", true)]
    [InlineData("stayDuration", "greaterThan", "2", "AND", true)]
    [InlineData("stayDuration", "greaterThan", "3", "AND", false)]
    [InlineData("stayDuration", "lessThan", "4", "AND", true)]
    [InlineData("stayDuration", "between", "[2,3]", "AND", true)]
    [InlineData("stayDuration", "between", "[4,9]", "AND", false)]
    [InlineData("guestCount", "equals", "2", "AND", true)]
    [InlineData("bookingLeadTime", "equals", "10", "AND", true)]
    public async Task Trigger_ConditionRules_AreEvaluatedOnTheBookingOfTheEvent(string fieldId, string op, string valueJson, string groupOperator, bool met)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(
            OrgA,
            BookingSource.Airbnb,
            checkIn: new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            checkOut: new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc));
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", Rule(fieldId, op, valueJson, groupOperator));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        Assert.Equal(met ? 1 : 0, (await rig.ExecutionsAsync()).Count);
    }

    [Fact]
    public async Task Trigger_PropertyOfTheBooking_IsAFactOfTheCondition()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", Rule("propertyId", "in", $"[\"{booking.PropertyId}\"]"));
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", Rule("propertyId", "in", $"[\"{Guid.NewGuid()}\"]"));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        Assert.Single(await rig.ExecutionsAsync());
    }

    [Fact]
    public async Task Trigger_OrGroup_IsMetByOneRule()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb);
        const string either =
            """{"version":"1.0","groups":[{"operator":"OR","rules":[{"fieldId":"channel","operator":"equals","value":"Direct"},{"fieldId":"guestCount","operator":"equals","value":2}]}]}""";
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", either);

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        Assert.Single(await rig.ExecutionsAsync());
    }

    /// <summary>A rule on a fact nobody can prove (no booking in the event, a booking of another org) is not met: never "everything".</summary>
    [Fact]
    public async Task Trigger_ConditionOnAFactThatIsNotKnown_IsNotMet()
    {
        var rig = new RoutineRig();
        var otherOrg = await rig.SeedBookingAsync(OrgB, BookingSource.Airbnb);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", ChannelIs("Airbnb"));
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", Rule("channel", "notEquals", "\"Direct\""));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, new { Nothing = "to see" });
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(otherOrg));

        Assert.Empty(await rig.ExecutionsAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"XOR","rules":[]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"nope","operator":"equals","value":"x"}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"channel","operator":"between","value":[1,2]}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"channel","operator":"equals"}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"paymentStatus","operator":"equals","value":"paid"}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"totalStays","operator":"greaterThan","value":3}]}]}""")]
    public async Task Trigger_InvalidCondition_IsRecordedAsFailed_AndRunsNothing(string conditionJson)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, WaitAnHour, conditionJson);

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.NotNull(execution.CompletedAt);
        var result = RoutineRig.ResultOf(execution);
        Assert.Equal(RoutineCodes.ConditionInvalid, result.Code);
        Assert.Empty(result.Actions);
        Assert.Empty(rig.Scheduled);
    }

    // ─── Waits (AC-NF21-1, ADR-022-R10) ──────────────────────────────────────────

    [Fact]
    public async Task Wait_SchedulesTheResumeJobAtResumeAt_AndSavesThePayloadAndTheNextAction()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("Wait", """{"days":1,"hours":2}"""), ("NotifyOwner", null)));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        var resumeAt = RoutineRig.Now.UtcDateTime.AddHours(26);
        Assert.Equal(RoutineExecutionOutcomes.Waiting, execution.Outcome);
        Assert.Equal(resumeAt, execution.ResumeAt);
        Assert.Null(execution.CompletedAt);

        var (job, state) = Assert.Single(rig.Scheduled);
        Assert.Equal(typeof(RoutineDelayJob), job.Type);
        Assert.Equal(nameof(RoutineDelayJob.ResumeRoutineAsync), job.Method.Name);
        Assert.Equal(execution.Id, Assert.IsType<Guid>(job.Args[0]));
        Assert.Equal(resumeAt, Assert.IsType<ScheduledState>(state).EnqueueAt);

        var result = RoutineRig.ResultOf(execution);
        Assert.Equal(1, result.NextActionIndex);
        Assert.Equal([new RoutineActionResult(0, RoutineActionType.Wait, RoutineActionOutcomes.Waiting, null)], result.Actions);
        var saved = RoutinePayload.FromJson(result.PayloadJson);
        Assert.True(RoutinePayload.TryGetId(saved, "bookingId", out var savedBooking));
        Assert.Equal(booking.Id, savedBooking);
    }

    /// <summary>
    /// The execution that resumes reads the booking with what the event said: the access code guardrail, which needs the booking, lets it through
    /// (the old resume ran with an empty payload, so nothing after a wait could see the booking).
    /// </summary>
    [Fact]
    public async Task Resume_ContinuesWithTheSavedPayload()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedVerificationAsync(OrgA, booking, BiometricOutcome.Accept, RoutineRig.Now.UtcDateTime.AddHours(-2));
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("Wait", """{"hours":1}"""), ("SendAccessCode", null)));
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));
        var waiting = await rig.SingleExecutionAsync();

        await rig.ResumeAsync(waiting.Id, new FixedTimeProvider(RoutineRig.Now.AddHours(1)));

        var resumed = await rig.ExecutionAsync(waiting.Id);
        var result = RoutineRig.ResultOf(resumed);
        Assert.Equal(RoutineActionOutcomes.Done, result.Actions[0].Outcome);
        Assert.NotEqual(RoutineActionOutcomes.Blocked, result.Actions[1].Outcome);
        Assert.Equal(1, result.Actions[1].Index);
        Assert.Equal(RoutineActionType.SendAccessCode, result.Actions[1].Type);
    }

    /// <summary>
    /// The resumed execution goes on from the saved index, not from the number of actions that ran: a blocked action ran nothing, and counting the
    /// ones that ran would have waited again instead of going on.
    /// </summary>
    [Fact]
    public async Task Resume_ContinuesFromTheSavedIndex_NotFromTheNumberOfActionsThatRan()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb, BookingStatus.Pending);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("MessageToGuest", null), ("Wait", """{"hours":1}"""), ("NotifyOwner", null)));
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));
        var waiting = await rig.SingleExecutionAsync();
        var before = RoutineRig.ResultOf(waiting);
        Assert.Equal(2, before.NextActionIndex);
        Assert.Equal(RoutineActionOutcomes.Blocked, before.Actions[0].Outcome);
        Assert.Equal(RoutineActionOutcomes.Waiting, before.Actions[1].Outcome);

        await rig.ResumeAsync(waiting.Id, new FixedTimeProvider(RoutineRig.Now.AddHours(1)));

        var resumed = await rig.ExecutionAsync(waiting.Id);
        Assert.Equal(RoutineExecutionOutcomes.Failed, resumed.Outcome);
        Assert.Equal(
            [
                new RoutineActionResult(0, RoutineActionType.MessageToGuest, RoutineActionOutcomes.Blocked, RoutineCodes.AirbnbNotConfirmed),
                new RoutineActionResult(1, RoutineActionType.Wait, RoutineActionOutcomes.Done, null),
                new RoutineActionResult(2, RoutineActionType.NotifyOwner, RoutineActionOutcomes.Failed, RoutineCodes.ActionNotAvailable),
            ],
            RoutineRig.ResultOf(resumed).Actions);
        Assert.Single(rig.Scheduled);
    }

    /// <summary>Proof (data rule): two resume jobs for the same wait run the remaining actions once (ADR-022-R10).</summary>
    [Fact]
    public async Task RoutineEngine_ResumeTwice_ContinuesOnce_Test()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("Wait", """{"hours":1}"""), ("NotifyOwner", null), ("Wait", """{"hours":1}""")));
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));
        var waiting = await rig.SingleExecutionAsync();
        var later = new FixedTimeProvider(RoutineRig.Now.AddHours(1));

        await rig.ResumeAsync(waiting.Id, later);
        var once = await rig.ExecutionAsync(waiting.Id);
        await rig.ResumeAsync(waiting.Id, later);
        await rig.ResumeAsync(waiting.Id, new FixedTimeProvider(RoutineRig.Now.AddDays(3)));

        var twice = await rig.ExecutionAsync(waiting.Id);
        Assert.Equal(once.Outcome, twice.Outcome);
        Assert.Equal(once.CompletedAt, twice.CompletedAt);
        Assert.Equal(once.ResultJson, twice.ResultJson);
        Assert.Single(RoutineRig.ResultOf(twice).Actions, a => a.Index == 1);
        Assert.Single(rig.Scheduled);
    }

    [Fact]
    public async Task Resume_AnExecutionThatIsNotWaiting_OrDoesNotExist_DoesNothing()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]");
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));
        var finished = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Success, finished.Outcome);

        await rig.ResumeAsync(finished.Id);
        await rig.ResumeAsync(Guid.NewGuid());

        var unchanged = await rig.SingleExecutionAsync();
        Assert.Equal(finished.CompletedAt, unchanged.CompletedAt);
        Assert.Equal(finished.ResultJson, unchanged.ResultJson);
    }

    [Fact]
    public async Task Resume_RoutineDeactivatedMeanwhile_EndsTheExecutionFailed()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        var routine = await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("Wait", """{"hours":1}"""), ("NotifyOwner", null)));
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));
        var waiting = await rig.SingleExecutionAsync();
        await rig.SetActiveAsync(routine.Id, active: false);

        await rig.ResumeAsync(waiting.Id, new FixedTimeProvider(RoutineRig.Now.AddHours(1)));

        var ended = await rig.ExecutionAsync(waiting.Id);
        Assert.Equal(RoutineExecutionOutcomes.Failed, ended.Outcome);
        Assert.NotNull(ended.CompletedAt);
        var result = RoutineRig.ResultOf(ended);
        Assert.Equal(RoutineCodes.Inactive, result.Code);
        Assert.Single(result.Actions);
    }

    // ─── Actions with no implementation, test mode, the result (AC-NF21-1) ───────

    [Theory]
    [InlineData(nameof(RoutineActionType.NotifyOwner))]
    [InlineData(nameof(RoutineActionType.NotifyStaff))]
    [InlineData(nameof(RoutineActionType.NotifyTeam))]
    [InlineData(nameof(RoutineActionType.RequestReview))]
    [InlineData(nameof(RoutineActionType.AddTag))]
    public async Task Action_WithNoImplementation_FailsWithACode_AndNeverPretends(string type)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.Actions((type, null)));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        var action = Assert.Single(RoutineRig.ResultOf(execution).Actions);
        Assert.Equal(new RoutineActionResult(0, Enum.Parse<RoutineActionType>(type), RoutineActionOutcomes.Failed, RoutineCodes.ActionNotAvailable), action);
        Assert.DoesNotContain("sent", execution.ResultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("created", execution.ResultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requested", execution.ResultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execution_StopsAtTheFirstFailedAction_AndABlockedActionDoesNotStopTheNext()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb, BookingStatus.Pending);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("MessageToGuest", null), ("NotifyOwner", null), ("Wait", """{"hours":1}""")));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(
            [
                new RoutineActionResult(0, RoutineActionType.MessageToGuest, RoutineActionOutcomes.Blocked, RoutineCodes.AirbnbNotConfirmed),
                new RoutineActionResult(1, RoutineActionType.NotifyOwner, RoutineActionOutcomes.Failed, RoutineCodes.ActionNotAvailable),
            ],
            RoutineRig.ResultOf(execution).Actions);
        Assert.Empty(rig.Scheduled);
    }

    [Fact]
    public async Task Execution_WithOnlyBlockedActions_EndsSuccess()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb, BookingStatus.Pending);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.Actions(("MessageToGuest", null)));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Success, execution.Outcome);
        Assert.Equal(RoutineActionOutcomes.Blocked, Assert.Single(RoutineRig.ResultOf(execution).Actions).Outcome);
    }

    [Fact]
    public async Task TestMode_SimulatesEveryAction_WithNoEffect()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("Wait", """{"days":2}"""), ("NotifyOwner", null), ("AddTag", """{"tag":"vip"}""")),
            testMode: true);

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Success, execution.Outcome);
        Assert.Null(execution.ResumeAt);
        Assert.Equal(
            [
                new RoutineActionResult(0, RoutineActionType.Wait, RoutineActionOutcomes.Simulated, null),
                new RoutineActionResult(1, RoutineActionType.NotifyOwner, RoutineActionOutcomes.Simulated, null),
                new RoutineActionResult(2, RoutineActionType.AddTag, RoutineActionOutcomes.Simulated, null),
            ],
            RoutineRig.ResultOf(execution).Actions);
        Assert.Empty(rig.Scheduled);
    }

    /// <summary>The guardrails run in test mode too: the simulation shows what the real run would stop.</summary>
    [Fact]
    public async Task TestMode_StillShowsWhatAGuardrailBlocks()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb, BookingStatus.Pending);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.Actions(("MessageToGuest", null)), testMode: true);

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var action = Assert.Single(RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Actions);
        Assert.Equal(RoutineActionOutcomes.Blocked, action.Outcome);
        Assert.Equal(RoutineCodes.AirbnbNotConfirmed, action.Code);
    }

    [Fact]
    public async Task Execution_ResultJson_IsTheStructuredResult_CamelCaseWithEnumNames()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.Actions(("AddTag", null)));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        using var document = JsonDocument.Parse(execution.ResultJson);
        var action = document.RootElement.GetProperty("actions")[0];
        Assert.Equal(0, action.GetProperty("index").GetInt32());
        Assert.Equal("AddTag", action.GetProperty("type").GetString());
        Assert.Equal("failed", action.GetProperty("outcome").GetString());
        Assert.Equal("routine_action_not_available", action.GetProperty("code").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("nextActionIndex").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("payloadJson", out _));
    }

    [Fact]
    public async Task Execution_ActionsThatCannotBeRead_FailWithACode()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "not a list of actions");

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(RoutineCodes.ActionsInvalid, RoutineRig.ResultOf(execution).Code);
    }

    [Theory]
    [InlineData("""{"days":0}""")]
    [InlineData("""{"hours":-1}""")]
    [InlineData("""{"days":366}""")]
    [InlineData("""{"days":"2"}""")]
    [InlineData("{}")]
    public async Task Wait_OutOfRange_FailsTheAction_AndSchedulesNothing(string config)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.Actions(("Wait", config)));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(RoutineCodes.ActionsInvalid, Assert.Single(RoutineRig.ResultOf(execution).Actions).Code);
        Assert.Empty(rig.Scheduled);
    }

    // ─── Failures, redelivery, tenants ──────────────────────────────────────────

    /// <summary>
    /// A failure outside the database that nobody expected (the queue that schedules the resume is down) is not swallowed: the execution says so with a
    /// code and the failure goes on, so the dispatcher records it on the event.
    /// </summary>
    [Fact]
    public async Task Wait_WhenTheJobCannotBeScheduled_RecordsTheFailureAndRethrows()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, WaitAnHour);
        rig.Jobs.Setup(client => client.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("storage is down"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking)));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(RoutineCodes.WaitNotScheduled, RoutineRig.ResultOf(execution).Code);
        Assert.DoesNotContain("storage", execution.ResultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Trigger_TheSameEventTwice_RunsTheRoutineOnce()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, WaitAnHour);

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking), eventId: "event-1");
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking), eventId: "event-1");

        Assert.Single(await rig.ExecutionsAsync());
        Assert.Single(rig.Scheduled);
    }

    [Fact]
    public async Task Trigger_RunsOnlyTheActiveRoutinesOfTheOrgAndTheEventType()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        var mine = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]");
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", active: false);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingCanceled, "[]");
        await rig.SeedRoutineAsync(OrgB, RoutineEventType.BookingConfirmed, "[]");

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(mine.Id, execution.RoutineId);
        Assert.Equal(OrgA, execution.OrgId);
    }

    /// <summary>The payload of the bus is a dictionary of JSON values with the names the producer chose; a test or a service may pass any object.</summary>
    [Fact]
    public async Task Trigger_PayloadInAnyShape_NamesTheBookingInAnyCase()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]", ChannelIs("Airbnb"));
        var json = $$"""{"bookingId":"{{booking.Id}}"}""";
        object[] payloads =
        [
            new { BookingId = booking.Id },
            new { bookingId = booking.Id.ToString() },
            new Dictionary<string, object?> { ["BOOKINGID"] = booking.Id },
            new Dictionary<string, object?> { ["bookingId"] = JsonDocument.Parse(json).RootElement.GetProperty("bookingId").Clone() },
            RoutineRig.Json(json),
        ];

        foreach (var payload in payloads)
            await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, payload);

        Assert.Equal(payloads.Length, (await rig.ExecutionsAsync()).Count);
    }

    // ─── Simulation (AC-NF26-7) ──────────────────────────────────────────────────

    [Fact]
    public async Task Simulate_ReportsTheConditionAndTheGuardrails_AndWritesNothing()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb, BookingStatus.Pending);
        var routine = await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("Wait", """{"hours":1}"""), ("MessageToGuest", """{"templateId":"welcome"}""")),
            ChannelIs("Airbnb"));

        await using var db = rig.NewDb();
        var result = await rig.Service(db).SimulateAsync(OrgA, routine.Id, RoutineRig.PayloadOf(booking));

        Assert.True(result.ConditionsMatched);
        Assert.True(result.WouldExecute);
        Assert.Null(result.Code);
        Assert.Equal(routine.Name, result.RoutineName);
        Assert.Equal(2, result.Actions.Count);
        Assert.False(result.Actions[0].WouldBlock);
        Assert.True(result.Actions[1].WouldBlock);
        Assert.Equal(RoutineCodes.AirbnbNotConfirmed, result.Actions[1].Code);
        Assert.Equal("welcome", result.Actions[1].Config.GetProperty("templateId").GetString());
        Assert.Empty(await rig.ExecutionsAsync());
        Assert.Empty(rig.Scheduled);
    }

    [Fact]
    public async Task Simulate_UnmetOrInvalidCondition_WouldNotExecute()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Direct);
        var unmet = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, WaitAnHour, ChannelIs("Airbnb"));
        var invalid = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, WaitAnHour, "not json");

        await using var db = rig.NewDb();
        var service = rig.Service(db);
        var notMet = await service.SimulateAsync(OrgA, unmet.Id, RoutineRig.PayloadOf(booking));
        var broken = await service.SimulateAsync(OrgA, invalid.Id, RoutineRig.PayloadOf(booking));

        Assert.False(notMet.ConditionsMatched);
        Assert.False(notMet.WouldExecute);
        Assert.Null(notMet.Code);
        Assert.False(broken.WouldExecute);
        Assert.Equal(RoutineCodes.ConditionInvalid, broken.Code);
    }

    [Fact]
    public async Task Simulate_RoutineOfAnotherOrg_IsNotFound()
    {
        var rig = new RoutineRig();
        var routine = await rig.SeedRoutineAsync(OrgB, RoutineEventType.BookingConfirmed, "[]");

        await using var db = rig.NewDb();
        var error = await Assert.ThrowsAsync<NotFoundException>(() => rig.Service(db).SimulateAsync(OrgA, routine.Id, new { }));

        Assert.Equal(RoutineCodes.NotFound, error.Code);
    }

    // ─── The job ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RoutineDelayJob_RunsOnePerExecutionAtATime_AndRetriesAFewTimes()
    {
        var method = typeof(RoutineDelayJob).GetMethod(nameof(RoutineDelayJob.ResumeRoutineAsync))!;

        var concurrency = Assert.Single(method.GetCustomAttributes(typeof(Hangfire.DisableConcurrentExecutionAttribute), inherit: false)
            .Cast<Hangfire.DisableConcurrentExecutionAttribute>());
        Assert.Contains("{0}", concurrency.Resource, StringComparison.Ordinal);
        Assert.Equal(RoutineDelayJob.LockTimeoutSeconds, concurrency.TimeoutSec);
        var retry = Assert.Single(method.GetCustomAttributes(typeof(Hangfire.AutomaticRetryAttribute), inherit: false).Cast<Hangfire.AutomaticRetryAttribute>());
        Assert.Equal(RoutineDelayJob.MaxAttempts, retry.Attempts);
    }

    [Fact]
    public async Task RoutineDelayJob_ResumesTheExecutionItIsGiven()
    {
        var calls = new List<Guid>();
        var service = new Mock<Casazen.Core.Services.IRoutineService>();
        service
            .Setup(s => s.ResumeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CancellationToken>((id, _) => calls.Add(id))
            .Returns(Task.CompletedTask);
        var job = new RoutineDelayJob(service.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<RoutineDelayJob>.Instance);
        var executionId = Guid.NewGuid();

        await job.ResumeRoutineAsync(executionId, CancellationToken.None);

        Assert.Equal([executionId], calls);
    }
}
