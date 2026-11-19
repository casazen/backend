using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F07/US01/T03 (ADR-022): the switch of the routine engine for the action <c>MessageToGuest</c>. With <c>Features:ScheduledMessages</c> on the action goes through the plan (one row for the booking, the routine and
/// the action, the one dispatcher that sends); with it off it does what it did before and the plan is never asked: one switch and one send for each state of the flag, never two sends for one action.
/// </summary>
public class ScheduledMessageRoutineEngineTests
{
    private static readonly Guid OrgA = RoutineRig.OrgA;

    private static string MessageToGuest(Guid templateId) => $$$"""[{"type":"MessageToGuest","config":{"templateId":"{{{templateId}}}"}}]""";

    [Theory]
    [InlineData(RoutineActionOutcomes.Done, null)]
    [InlineData(RoutineActionOutcomes.Blocked, "variable_missing:check_in_method")]
    [InlineData(RoutineActionOutcomes.Skipped, "person")]
    [InlineData(RoutineActionOutcomes.Failed, "octorate_request_rejected")]
    public async Task WithTheModuleOn_TheActionGoesThroughThePlan_AndItsAnswerIsTheResultOfTheAction(string outcome, string? code)
    {
        var rig = new RoutineRig();
        rig.Flags.Set(FeatureFlags.ScheduledMessages, true);
        var messages = new Mock<IScheduledMessageService>();
        rig.ScheduledMessages = messages.Object;
        var booking = await rig.SeedBookingAsync(OrgA);
        var routine = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, MessageToGuest(Guid.NewGuid()));
        messages
            .Setup(m => m.OnRoutineActionAsync(routine.Id, 0, booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutineActionResult(0, RoutineActionType.MessageToGuest, outcome, code));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        messages.Verify(m => m.OnRoutineActionAsync(routine.Id, 0, booking.Id, It.IsAny<CancellationToken>()), Times.Once);
        var action = Assert.Single(RoutineExecutionResult.Parse((await rig.SingleExecutionAsync()).ResultJson).Actions);
        Assert.Equal((RoutineActionType.MessageToGuest, outcome, code), (action.Type, action.Outcome, action.Code));
    }

    [Fact]
    public async Task WithTheModuleOff_TheActionDoesWhatItDidBefore_AndThePlanIsNeverAsked()
    {
        var rig = new RoutineRig();
        var messages = new Mock<IScheduledMessageService>();
        rig.ScheduledMessages = messages.Object;
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, MessageToGuest(Guid.NewGuid()));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        messages.VerifyNoOtherCalls();
        Assert.Single(RoutineExecutionResult.Parse((await rig.SingleExecutionAsync()).ResultJson).Actions);
    }

    [Fact]
    public async Task InTestMode_TheActionIsSimulated_AndNeitherThePlanNorAnyMessageIsTouched()
    {
        var rig = new RoutineRig();
        rig.Flags.Set(FeatureFlags.ScheduledMessages, true);
        var messages = new Mock<IScheduledMessageService>();
        rig.ScheduledMessages = messages.Object;
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, MessageToGuest(Guid.NewGuid()), testMode: true);

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        messages.VerifyNoOtherCalls();
        Assert.Equal(RoutineActionOutcomes.Simulated, Assert.Single(RoutineExecutionResult.Parse((await rig.SingleExecutionAsync()).ResultJson).Actions).Outcome);
    }
}
