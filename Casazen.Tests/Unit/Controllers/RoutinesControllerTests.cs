using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Assistant;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Routines;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E01/F04/US01/T03 (NF-21, ADR-022): the routines page's endpoints. What the controller adds to the service: the flag, the policies, the org of the caller,
/// the reach (a routine acts on every property of the org, so a collaborator limited to some properties changes nothing and sees none), and the shapes
/// the frontend client reads. The rules of what is saved are proved on the service (<see cref="RoutineManagementServiceTests"/>).
/// </summary>
public class RoutinesControllerTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ─── The surface ─────────────────────────────────────────────────────────────

    /// <summary>The flag, the privacy of the answers and the exact list of endpoints, each with its verb, route and policy: a change to any of them changes this list.</summary>
    [Fact]
    public void TheController_IsBehindTheFlag_Private_AndEveryEndpointHasItsVerbRouteAndPolicy()
    {
        var type = typeof(RoutinesController);
        Assert.Equal(FeatureFlags.Routines, Assert.Single(type.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());
        Assert.Equal("api/routines", Assert.Single(type.GetCustomAttributes<RouteAttribute>()).Template);
        Assert.Equal([CasazenPolicies.PropertyRead], type.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));

        var endpoints = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method =>
            {
                var http = Assert.Single(method.GetCustomAttributes<HttpMethodAttribute>());
                Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
                var policies = method.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy);
                return $"{Assert.Single(http.HttpMethods)} {http.Template}|{method.Name}|{string.Join('+', policies)}";
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        string[] expected =
        [
            "GET |List|",
            "GET actions|Actions|",
            "GET {id:guid}|Get|",
            $"POST suggest-action|SuggestAction|{CasazenPolicies.PropertyWrite}",
            $"POST |Create|{CasazenPolicies.PropertyWrite}",
            $"PUT {{id:guid}}|Update|{CasazenPolicies.PropertyWrite}",
            $"DELETE {{id:guid}}|Delete|{CasazenPolicies.PropertyWrite}",
            $"PUT {{id:guid}}/active|SetActive|{CasazenPolicies.PropertyWrite}",
            $"PUT {{id:guid}}/test-mode|SetTestMode|{CasazenPolicies.PropertyWrite}",
            "GET {id:guid}/executions|Executions|",
            $"POST {{id:guid}}/simulate|Simulate|{CasazenPolicies.PropertyWrite}",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), endpoints);
    }

    [Fact]
    public void TheSwitchesAreRequired_SoABodyWithoutTheValueIsNotReadAsOff()
    {
        var noActive = new SetRoutineActiveDto();
        var noTestMode = new SetRoutineTestModeDto();
        var off = new SetRoutineActiveDto { Active = false };
        var active = new List<ValidationResult>();
        var testMode = new List<ValidationResult>();

        var noActiveValid = Validator.TryValidateObject(noActive, new ValidationContext(noActive), active, validateAllProperties: true);
        var noTestModeValid = Validator.TryValidateObject(noTestMode, new ValidationContext(noTestMode), testMode, validateAllProperties: true);
        var offValid = Validator.TryValidateObject(off, new ValidationContext(off), new List<ValidationResult>(), validateAllProperties: true);

        Assert.False(noActiveValid);
        Assert.False(noTestModeValid);
        Assert.Equal("RoutineActiveRequired", Assert.Single(active).ErrorMessage);
        Assert.Equal("RoutineTestModeRequired", Assert.Single(testMode).ErrorMessage);
        Assert.True(offValid);
    }

    // ─── The reach (permission rule) ─────────────────────────────────────────────

    /// <summary>
    /// Proof (permission rule): a collaborator limited to some properties changes no routine and simulates none: every write is a 403
    /// <c>routines_need_org_scope</c> and nothing is asked of the services (strict mocks), because a routine acts on every property of the org.
    /// </summary>
    [Fact]
    public async Task Routines_CollaboratorWithLimitedScope_CannotWrite_Test()
    {
        var management = new Mock<IRoutineManagementService>(MockBehavior.Strict);
        var engine = new Mock<IRoutineService>(MockBehavior.Strict);
        var controller = Controller(management.Object, engine.Object, administrator: false);
        var id = Guid.NewGuid();

        var answers = new[]
        {
            ProblemOf(await controller.Create(new CreateRoutineDto { Name = "Prova", EventType = "BookingConfirmed" }, Ct)),
            ProblemOf(await controller.Update(id, new UpdateRoutineDto { Name = "Altro" }, Ct)),
            ProblemOf(await controller.Delete(id, Ct)),
            ProblemOf(await controller.SetActive(id, new SetRoutineActiveDto { Active = true }, Ct)),
            ProblemOf(await controller.SetTestMode(id, new SetRoutineTestModeDto { TestMode = true }, Ct)),
            ProblemOf(await controller.SuggestAction(new RoutineInstructionDto("Organizza le pulizie"), new Mock<IAiProvider>(MockBehavior.Strict).Object, Ct)),
            ProblemOf(await controller.Simulate(id, RoutineRig.Json("""{"bookingId":"b"}"""), Ct)),
        };

        Assert.All(answers, answer => AssertProblem(answer, StatusCodes.Status403Forbidden, RoutineCodes.NeedsOrgScope));
        management.VerifyNoOtherCalls();
        engine.VerifyNoOtherCalls();
    }

    /// <summary>The same caller reads none: an empty list, a 404 for one routine and for its log (as an item of the org is in every restricted scope), and the services are not asked.</summary>
    [Fact]
    public async Task CollaboratorWithLimitedScope_ReadsNoRoutine()
    {
        var management = new Mock<IRoutineManagementService>(MockBehavior.Strict);
        var engine = new Mock<IRoutineService>(MockBehavior.Strict);
        var controller = Controller(management.Object, engine.Object, administrator: false);
        var id = Guid.NewGuid();

        var list = Assert.IsType<OkObjectResult>((await controller.List(Ct)).Result);
        var one = ProblemOf(await controller.Get(id, Ct));
        var log = ProblemOf(await controller.Executions(id, Ct));

        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<RoutineDto>>(list.Value));
        AssertProblem(one, StatusCodes.Status404NotFound, RoutineCodes.NotFound);
        AssertProblem(log, StatusCodes.Status404NotFound, RoutineCodes.NotFound);
        management.VerifyNoOtherCalls();
        engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ACallerWithNoOrg_Is401_AndACallerWithNoReach_Is403_AndNothingIsAsked()
    {
        var management = new Mock<IRoutineManagementService>(MockBehavior.Strict);
        var engine = new Mock<IRoutineService>(MockBehavior.Strict);
        var noReach = new Mock<IHostScopeResolver>();
        noReach
            .Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostScope?)null);

        var withoutOrg = Controller(management.Object, engine.Object, administrator: true, withOrg: false);
        var deactivated = Controller(management.Object, engine.Object, administrator: true, scopes: noReach.Object);

        AssertProblem(ProblemOf(await withoutOrg.List(Ct)), StatusCodes.Status401Unauthorized, ProblemCodes.Unauthorized);
        AssertProblem(ProblemOf(await withoutOrg.Create(new CreateRoutineDto(), Ct)), StatusCodes.Status401Unauthorized, ProblemCodes.Unauthorized);
        AssertProblem(ProblemOf(await deactivated.List(Ct)), StatusCodes.Status403Forbidden, ProblemCodes.Forbidden);
        AssertProblem(ProblemOf(await deactivated.Delete(Guid.NewGuid(), Ct)), StatusCodes.Status403Forbidden, ProblemCodes.Forbidden);
        management.VerifyNoOtherCalls();
        engine.VerifyNoOtherCalls();
    }

    // ─── The org-wide caller: the shapes the client reads ────────────────────────

    [Fact]
    public async Task List_AndGet_AnswerTheRoutinesOfTheOrg_InTheShapeOfTheClient()
    {
        var first = NewRoutine("Controlla l'arrivo");
        var second = NewRoutine("Chiudi la casa");
        var management = new Mock<IRoutineManagementService>(MockBehavior.Strict);
        management.Setup(m => m.ListAsync(_world.OrgId, It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        management.Setup(m => m.GetAsync(_world.OrgId, first.Id, It.IsAny<CancellationToken>())).ReturnsAsync(first);
        var controller = Controller(management.Object, new Mock<IRoutineService>(MockBehavior.Strict).Object, administrator: true);

        var list = Assert.IsAssignableFrom<IEnumerable<RoutineDto>>(Assert.IsType<OkObjectResult>((await controller.List(Ct)).Result).Value).ToList();
        var one = Assert.IsType<RoutineDto>(Assert.IsType<OkObjectResult>((await controller.Get(first.Id, Ct)).Result).Value);

        Assert.Equal(["Controlla l'arrivo", "Chiudi la casa"], list.Select(r => r.Name));
        Assert.Equal(first.Id, one.Id);
        Assert.Equal(_world.OrgId, one.OrgId);
        Assert.Equal("BeforeCheckin", one.EventType);
        Assert.Equal(first.EventConfig, one.EventConfig);
        Assert.Equal(first.ConditionJson, one.ConditionJson);
        Assert.Equal(first.ActionsJson, one.ActionsJson);

        // routine.types.ts: id, orgId, name, eventType, eventConfig, conditionJson, actionsJson, testMode, active.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(one, Web));
        Assert.Equal(
            ["actionsJson", "active", "conditionJson", "eventConfig", "eventType", "id", "name", "orgId", "testMode"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("eventConfig").ValueKind);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("actionsJson").ValueKind);
    }

    [Fact]
    public async Task Create_PassesWhatTheBodySaysToTheServiceWithTheOrgOfTheCaller_AndAnswers201WithTheRoutine()
    {
        var saved = NewRoutine("Controlla l'arrivo");
        RoutineCommand? seen = null;
        var management = new Mock<IRoutineManagementService>(MockBehavior.Strict);
        management
            .Setup(m => m.CreateAsync(_world.OrgId, It.IsAny<RoutineCommand>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, RoutineCommand, CancellationToken>((_, command, _) => seen = command)
            .ReturnsAsync(saved);
        var controller = Controller(management.Object, new Mock<IRoutineService>(MockBehavior.Strict).Object, administrator: true);
        var body = JsonSerializer.Deserialize<CreateRoutineDto>(
            """
            {"name":"Controlla l'arrivo","eventType":"BeforeCheckin","eventConfig":{"days":2},"condition":{"version":"1.0","groups":[]},
             "actions":[{"type":"Wait","config":{"hours":2}},{"type":"CreateActivity","config":{"text":"Chiavi"}}],"testMode":true}
            """,
            Web)!;

        var answer = await controller.Create(body, Ct);

        var created = Assert.IsType<CreatedAtActionResult>(answer.Result);
        Assert.Equal(nameof(RoutinesController.Get), created.ActionName);
        Assert.Equal(saved.Id, created.RouteValues!["id"]);
        Assert.Equal(saved.Id, Assert.IsType<RoutineDto>(created.Value).Id);
        Assert.NotNull(seen);
        Assert.Equal("Controlla l'arrivo", seen.Name);
        Assert.Equal("BeforeCheckin", seen.EventType);
        Assert.Equal("""{"days":2}""", seen.EventConfig!.Value.GetRawText());
        Assert.Equal(JsonValueKind.Object, seen.Condition!.Value.ValueKind);
        Assert.Equal(["Wait", "CreateActivity"], seen.Actions!.Select(a => a.Type));
        Assert.Equal("""{"hours":2}""", seen.Actions![0].Config!.Value.GetRawText());
        Assert.True(seen.TestMode);
        Assert.Null(seen.Active);
    }

    [Fact]
    public async Task Update_Delete_AndTheSwitches_ForwardTheIdAndTheValueToTheService()
    {
        var routine = NewRoutine("Controlla l'arrivo");
        RoutineCommand? seen = null;
        var management = new Mock<IRoutineManagementService>(MockBehavior.Strict);
        management
            .Setup(m => m.UpdateAsync(_world.OrgId, routine.Id, It.IsAny<RoutineCommand>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, RoutineCommand, CancellationToken>((_, _, command, _) => seen = command)
            .ReturnsAsync(routine);
        management.Setup(m => m.DeleteAsync(_world.OrgId, routine.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        management.Setup(m => m.SetActiveAsync(_world.OrgId, routine.Id, true, It.IsAny<CancellationToken>())).ReturnsAsync(routine);
        management.Setup(m => m.SetTestModeAsync(_world.OrgId, routine.Id, false, It.IsAny<CancellationToken>())).ReturnsAsync(routine);
        var controller = Controller(management.Object, new Mock<IRoutineService>(MockBehavior.Strict).Object, administrator: true);

        var updated = await controller.Update(routine.Id, new UpdateRoutineDto { Name = "Nuovo nome" }, Ct);
        var deleted = await controller.Delete(routine.Id, Ct);
        var switchedOn = await controller.SetActive(routine.Id, new SetRoutineActiveDto { Active = true }, Ct);
        var testModeOff = await controller.SetTestMode(routine.Id, new SetRoutineTestModeDto { TestMode = false }, Ct);

        Assert.IsType<OkObjectResult>(updated.Result);
        Assert.IsType<NoContentResult>(deleted);
        Assert.IsType<OkObjectResult>(switchedOn.Result);
        Assert.IsType<OkObjectResult>(testModeOff.Result);
        Assert.Equal("Nuovo nome", seen!.Name);
        Assert.Null(seen.EventType);
        Assert.Null(seen.Actions);
        management.VerifyAll();
    }

    [Fact]
    public async Task Executions_AreThePageOfTheLog_WithTheTotalInTheHeader_AndNeverTheSavedPayload()
    {
        var routine = NewRoutine("Controlla l'arrivo");
        var waiting = new RoutineExecution
        {
            OrgId = _world.OrgId,
            RoutineId = routine.Id,
            EventId = "booking-1",
            Outcome = RoutineExecutionOutcomes.Waiting,
            ResumeAt = HostScopeScenario.Now.UtcDateTime.AddHours(2),
            ResultJson = new RoutineExecutionResult(
                [RoutineActionResult.Waiting(0, RoutineActionType.Wait)],
                """{"bookingId":"7f0e2c7e-0000-4000-8000-000000000001"}""",
                1).ToJson(),
        };
        var management = new Mock<IRoutineManagementService>(MockBehavior.Strict);
        management
            .Setup(m => m.ListExecutionsAsync(_world.OrgId, routine.Id, 2, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoutineExecutionPage([waiting], 31, 2, 25));
        var controller = Controller(management.Object, new Mock<IRoutineService>(MockBehavior.Strict).Object, administrator: true);

        var answer = await controller.Executions(routine.Id, Ct, page: 2, pageSize: 25);

        var items = Assert.IsAssignableFrom<IEnumerable<RoutineExecutionDto>>(Assert.IsType<OkObjectResult>(answer.Result).Value).ToList();
        var item = Assert.Single(items);
        Assert.Equal("31", controller.Response.Headers["X-Total-Count"].ToString());
        Assert.Equal(RoutineExecutionOutcomes.Waiting, item.Outcome);
        Assert.Equal(waiting.ResumeAt, item.ResumeAt);
        var result = RoutineExecutionResult.Parse(item.ResultJson);
        Assert.Null(result.PayloadJson);
        Assert.Equal(1, result.NextActionIndex);
        Assert.Equal(RoutineActionOutcomes.Waiting, Assert.Single(result.Actions).Outcome);
        Assert.DoesNotContain("7f0e2c7e", item.ResultJson, StringComparison.Ordinal);
    }

    // ─── The simulation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Simulate_AsksTheEngineWithTheOrgOfTheCaller_AndTheBodyAsItIs()
    {
        var routine = NewRoutine("Controlla l'arrivo");
        object? seen = null;
        var simulation = new RoutineSimulationResult(routine.Id, routine.Name, ConditionsMatched: true, WouldExecute: true, []);
        var engine = new Mock<IRoutineService>(MockBehavior.Strict);
        engine
            .Setup(e => e.SimulateAsync(_world.OrgId, routine.Id, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, object, CancellationToken>((_, _, payload, _) => seen = payload)
            .ReturnsAsync(simulation);
        var controller = Controller(new Mock<IRoutineManagementService>(MockBehavior.Strict).Object, engine.Object, administrator: true);

        var answer = await controller.Simulate(routine.Id, RoutineRig.Json("""{"bookingId":"b-1"}"""), Ct);

        Assert.Same(simulation, Assert.IsType<OkObjectResult>(answer.Result).Value);
        Assert.Equal("b-1", Assert.IsType<JsonElement>(seen).GetProperty("bookingId").GetString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"b-1\"")]
    [InlineData("3")]
    [InlineData("null")]
    public async Task Simulate_ABodyThatIsNotAnObject_Is422_AndTheEngineIsNotAsked(string body)
    {
        var engine = new Mock<IRoutineService>(MockBehavior.Strict);
        var controller = Controller(new Mock<IRoutineManagementService>(MockBehavior.Strict).Object, engine.Object, administrator: true);

        var answer = await controller.Simulate(Guid.NewGuid(), RoutineRig.Json(body), Ct);

        AssertProblem(ProblemOf(answer), StatusCodes.Status422UnprocessableEntity, RoutineCodes.SimulationPayloadInvalid);
        engine.VerifyNoOtherCalls();
    }

    // ─── The catalog of the actions (AC-NF21-1) ──────────────────────────────────

    /// <summary>Every action of the enum, in its order, with the availability the engine and the activation read: the page shows what is real and what is not.</summary>
    [Fact]
    public void Actions_ListEveryActionWithItsAvailability_FromTheCatalogTheEngineReads()
    {
        var flags = new TestFeatureFlags(FeatureFlags.Routines);
        var controller = Controller(new Mock<IRoutineManagementService>(MockBehavior.Strict).Object, new Mock<IRoutineService>(MockBehavior.Strict).Object, administrator: false);

        var items = Assert.IsAssignableFrom<IEnumerable<RoutineActionCatalogItemDto>>(Assert.IsType<OkObjectResult>(controller.Actions().Result).Value).ToList();

        Assert.Equal(Enum.GetNames<RoutineActionType>(), items.Select(i => i.Type));
        Assert.All(items, item =>
        {
            var expected = RoutineActionCatalog.Of(Enum.Parse<RoutineActionType>(item.Type), flags);
            Assert.Equal(expected.Available, item.Available);
            Assert.Equal(expected.Code, item.Code);
        });
        Assert.True(items.Single(i => i.Type == nameof(RoutineActionType.Wait)).Available);
        Assert.True(items.Single(i => i.Type == nameof(RoutineActionType.CreateActivity)).Available);
        var notYet = items.Single(i => i.Type == nameof(RoutineActionType.NotifyOwner));
        Assert.False(notYet.Available);
        Assert.Equal(RoutineCodes.ActionNotAvailable, notYet.Code);
    }

    [Theory]
    [InlineData("CreateCleaningRequest", "CreateCleaningRequest")]
    [InlineData("RequestReview", "RequestReview")]
    [InlineData("CreateActivity", null)]
    [InlineData("invented", null)]
    public async Task SuggestAction_OnlySuggestsConcreteAutomaticOperations(string modelAnswer, string? expected)
    {
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        ai.Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, It.IsAny<string>(), Ct))
            .ReturnsAsync(new AiGenerationResult(modelAnswer, 1, 1, AiModelTier.Economy, false));
        var controller = Controller(Mock.Of<IRoutineManagementService>(), Mock.Of<IRoutineService>(), administrator: true);
        var response = await controller.SuggestAction(new RoutineInstructionDto("Organizza le pulizie"), ai.Object, Ct);
        Assert.Equal(expected, Assert.IsType<RoutineActionSuggestion>(Assert.IsType<OkObjectResult>(response.Result).Value).Type);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private RoutinesController Controller(
        IRoutineManagementService management,
        IRoutineService engine,
        bool administrator,
        bool withOrg = true,
        IHostScopeResolver? scopes = null)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.OrgId).Returns(withOrg ? _world.OrgId : null);
        var caller = administrator ? AssistantTestKit.Administrator(_world) : AssistantTestKit.Collaborator(_world);
        return new RoutinesController(
            management,
            engine,
            new TestFeatureFlags(FeatureFlags.Routines),
            tenant.Object,
            scopes ?? AssistantTestKit.ScopeOf(_world, administrator),
            NullLogger<RoutinesController>.Instance)
        {
            ControllerContext = AssistantTestKit.ContextOf(caller),
        };
    }

    private Routine NewRoutine(string name) =>
        new()
        {
            OrgId = _world.OrgId,
            Name = name,
            EventType = RoutineEventType.BeforeCheckin,
            EventConfig = """{"days":2}""",
            ConditionJson = "{}",
            ActionsJson = """[{"type":"CreateActivity","config":{"text":"Chiavi"}}]""",
        };

    private static ObjectResult ProblemOf<T>(ActionResult<T> answer) => Assert.IsType<ObjectResult>(answer.Result);

    private static ObjectResult ProblemOf(IActionResult answer) => Assert.IsType<ObjectResult>(answer);

    private static void AssertProblem(ObjectResult answer, int status, string code)
    {
        Assert.Equal(status, answer.StatusCode);
        Assert.Equal(code, Assert.IsAssignableFrom<ProblemDetails>(answer.Value).Extensions["code"]);
    }
}
