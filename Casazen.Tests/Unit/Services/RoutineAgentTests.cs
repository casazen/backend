using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class RoutineAgentTests
{
    private static readonly Guid OrgId = RoutineRig.OrgA;
    private const RoutineEventType Trigger = RoutineEventType.BookingConfirmed;

    [Theory]
    [InlineData("[0]", true, RoutineActionOutcomes.Done, null)]
    [InlineData("[]", true, RoutineActionOutcomes.Skipped, null)]
    [InlineData("[0,0]", true, null, RoutineCodes.AiPlanInvalid)]
    [InlineData("[1]", true, null, RoutineCodes.AiPlanInvalid)]
    [InlineData("{}", true, null, RoutineCodes.AiPlanInvalid)]
    [InlineData("UNKNOWN", true, null, RoutineCodes.AiFactsMissing)]
    [InlineData("[0]", false, null, RoutineCodes.AiUnavailable)]
    public async Task CustomAgentDispatchesOnlyAnAuthorizedPlan(string response, bool configured, string? outcome, string? error)
    {
        var rig = new RoutineRig();
        var stay = await rig.SeedBookingAsync(OrgId, guests: 3);
        var routine = await rig.SeedRoutineAsync(OrgId, Trigger, RoutineRig.Actions(("CreateActivity", """{"text":"check"}""")));
        await using var db = rig.NewDb();
        var objective = "Only for three guests";
        await rig.Management(db).UpdateAsync(OrgId, routine.Id, new RoutineCommand(null, null, null, null, null, null, null, objective));
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        ai.Setup(p => p.GenerateAsync(It.Is<AiRequest>(r => r.Prompt.Contains(objective) && r.Prompt.Contains("CreateActivity") && !r.Prompt.Contains(stay.Id.ToString())),
            AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(response, 1, 1, AiModelTier.Economy, false, configured));
        rig.Ai = ai.Object;

        await rig.TriggerAsync(OrgId, Trigger, RoutineRig.PayloadOf(stay), "same-event");
        await rig.TriggerAsync(OrgId, Trigger, RoutineRig.PayloadOf(stay), "same-event");
        var result = RoutineRig.ResultOf(await rig.SingleExecutionAsync());
        Assert.Equal(error, result.Code);
        Assert.Equal(outcome, result.Actions.SingleOrDefault()?.Outcome);
        Assert.Equal(outcome == RoutineActionOutcomes.Done ? 1 : 0, await db.InternalActivities.CountAsync());
        Assert.Single(ai.Invocations);
    }

    [Fact]
    public async Task ModuleRemovalStopsAWaitingAgentAndNewEvents()
    {
        var rig = new RoutineRig();
        var stay = await rig.SeedBookingAsync(OrgId);
        await rig.SeedRoutineAsync(OrgId, Trigger, RoutineRig.Actions(("Wait", """{"hours":1}"""), ("CreateActivity", """{"text":"check"}""")));
        await using var db = rig.NewDb();
        var org = await db.Orgs.SingleAsync();
        org.CommercialSignupJson = new CommercialSignup { PlanTier = "Pro", Addons = ["ai-agents"] }.Serialize();
        org.SubscriptionStatus = SubscriptionStatus.Active;
        org.CommercialPaymentConfirmed = true;
        org.CommercialPaidAddonsJson = """["ai-agents"]""";
        await db.SaveChangesAsync();
        await rig.TriggerAsync(OrgId, Trigger, RoutineRig.PayloadOf(stay));
        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Waiting, execution.Outcome);
        org.CommercialPaidAddonsJson = "[]";
        await db.SaveChangesAsync();
        await rig.TriggerAsync(OrgId, Trigger, RoutineRig.PayloadOf(stay));
        await rig.ResumeAsync(execution.Id, new FixedTimeProvider(RoutineRig.Now.AddHours(1)));
        Assert.Equal(RoutineCodes.AiSubscriptionRequired, RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Code);
        Assert.Empty(db.InternalActivities);
    }
}
