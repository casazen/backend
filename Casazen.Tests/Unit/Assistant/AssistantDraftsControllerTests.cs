using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Assistant;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US02/T01: the endpoints of the drafts are behind the flag and private, each with its policy and its limits; the simulator answers the 404 of a property that does not exist
/// for a property out of the caller's reach and runs nothing; a key that is not one is a 400 before anything runs.
/// </summary>
public sealed class AssistantDraftsControllerTests
{
    [Fact]
    public void TheController_IsBehindTheFlag_Private_AndEveryActionHasItsPolicyAndItsLimits()
    {
        var type = typeof(AssistantDraftsController);
        Assert.Equal(FeatureFlags.MessageAssistant, Assert.Single(type.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());

        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).ToList();
        Assert.Equal([nameof(AssistantDraftsController.Draft), nameof(AssistantDraftsController.Simulate)], actions.Select(a => a.Name).Order());
        Assert.All(actions, action => Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>()));
        Assert.All(actions, action => Assert.NotNull(action.GetCustomAttribute<AiRateLimitAttribute>()));

        var draft = type.GetMethod(nameof(AssistantDraftsController.Draft))!;
        Assert.Equal([CasazenPolicies.BookingWrite], draft.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));

        var simulate = type.GetMethod(nameof(AssistantDraftsController.Simulate))!;
        Assert.Equal([CasazenPolicies.PropertyWrite], simulate.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));
        Assert.Equal(RateLimitPolicies.AssistantSimulate, simulate.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
        var policy = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.AssistantSimulate);
        Assert.Equal((20, TimeSpan.FromHours(1), true), (policy.DefaultPermitLimit, policy.DefaultWindow, policy.PartitionByUser));
    }

    [Fact]
    public async Task Simulate_APropertyTheCollaboratorSoloAlcuniWasNotGiven_IsThe404OfAPropertyThatDoesNotExist_AndNothingRuns()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var drafts = new Mock<IAssistantDraftService>();
        drafts
            .Setup(d => d.SimulateAsync(world.OrgId, world.Granted.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssistantSimulation(AssistantDecision.NotHandled, AssistantDecisionCodes.AiProviderNotConfigured, null, null, SensitiveCategory.None, null, null, [], false, []));
        var controller = Controller(db, world, drafts.Object);

        foreach (var propertyId in new[] { world.Hidden.Id, world.OtherOrgProperty.Id, Guid.NewGuid() })
        {
            var problem = Assert.IsType<ObjectResult>((await controller.Simulate(new SimulateAssistantRequest { PropertyId = propertyId, Text = "Dove parcheggio?" }, CancellationToken.None)).Result);
            Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
            Assert.Equal("property_not_found", Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        }

        drafts.Verify(d => d.SimulateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // The property it was given is simulated.
        var granted = Assert.IsType<OkObjectResult>((await controller.Simulate(new SimulateAssistantRequest { PropertyId = world.Granted.Id, Text = "Dove parcheggio?" }, CancellationToken.None)).Result);
        Assert.Equal(AssistantDecisionCodes.AiProviderNotConfigured, Assert.IsType<AssistantSimulationDto>(granted.Value).Reason);
    }

    [Fact]
    public async Task Draft_AKeyThatIsNotOne_Is400_AndTheServiceIsNotAsked()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var drafts = new Mock<IAssistantDraftService>(MockBehavior.Strict);

        var problem = Assert.IsType<ObjectResult>((await Controller(db, world, drafts.Object).Draft("booking:123", CancellationToken.None)).Result);

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(MessageThreadKey.InvalidCode, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    [Fact]
    public async Task Draft_AsksTheServiceInTheReachOfTheCaller_AndAnswersTheDecisionWithItsSituationCode()
    {
        await using var db = AssistantTestKit.NewDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var key = MessageThreadKey.ForOta(Guid.NewGuid());
        var drafts = new Mock<IAssistantDraftService>();
        drafts
            .Setup(d => d.DraftNowAsync(It.Is<HostScope>(s => s.OrgId == world.OrgId), key, world.CollaboratorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssistantDraftOutcome(Guid.NewGuid(), AssistantDecision.Drafted, null, AssistantSituation.CheckInTime, AssistantUrgency.Normal, 0.9m, Guid.NewGuid(), null, false));

        var answer = Assert.IsType<OkObjectResult>((await Controller(db, world, drafts.Object).Draft(key.ToString(), CancellationToken.None)).Result);

        var dto = Assert.IsType<AssistantDraftOutcomeDto>(answer.Value);
        Assert.Equal((AssistantDecision.Drafted, "check_in_time"), (dto.Decision, dto.SituationCode));
    }

    private static AssistantDraftsController Controller(Casazen.Infrastructure.Data.AppDbContext db, HostScopeWorld world, IAssistantDraftService drafts) =>
        new(drafts, AssistantTestKit.Properties(db), AssistantTestKit.Org(world.OrgId), AssistantTestKit.ScopeOf(world), AssistantTestKit.Authorization(world))
        {
            ControllerContext = AssistantTestKit.ContextOf(AssistantTestKit.Collaborator(world)),
        };
}
