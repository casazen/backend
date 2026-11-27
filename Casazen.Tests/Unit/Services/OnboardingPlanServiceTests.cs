using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class OnboardingPlanServiceTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task OnboardingTodo_CarriesNoPersonalData_Test()
    {
        var service = Service();

        var deferred = await service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Listings, null, Ct);
        var left = await service.ExitAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Channels, Ct);

        var todos = await _db.InternalActivities.AsNoTracking().Where(a => a.OrgId == _world.OrgId).ToListAsync();
        Assert.Equal(2, todos.Count);
        var states = await _db.OnboardingPartStates.AsNoTracking().Where(s => s.OrgId == _world.OrgId).ToDictionaryAsync(s => s.PartKey);

        foreach (var (change, reason) in new[] { (deferred, OnboardingPlanCodes.ReasonDeferred), (left, OnboardingPlanCodes.ReasonExit) })
        {
            var todo = Assert.Single(todos, t => t.Id == change.TodoId);
            Assert.Equal(nameof(ActivityType.WizardResume), todo.ActivityType);
            Assert.Equal(ActivityAction.ResumeWizard, todo.Action);
            Assert.Equal(OnboardingPlanCodes.TodoEntityType, todo.EntityType);
            Assert.Equal(states[change.Key].Id, todo.EntityId);
            Assert.Equal(reason, todo.Reason);
            Assert.Equal(change.RemindAt, todo.Deadline);
            Assert.Null(todo.PropertyId);
            Assert.Null(todo.BookingId);
            Assert.Null(todo.StaffUserId);
            Assert.Null(todo.Notes);
            Assert.Null(todo.CreatedByUserId);
            Assert.Null(todo.InternalRate);

            var json = JsonSerializer.Serialize(new
            {
                todo.ActivityType,
                todo.Action,
                todo.EntityType,
                todo.EntityId,
                todo.Reason,
                todo.Status,
                todo.Notes,
                todo.StaffUserId,
                todo.CreatedByUserId,
                todo.ClosedBy,
            });
            foreach (var personal in new[] { _world.OwnerUserId, _world.Granted.Name, _world.Hidden.Name, _world.Granted.Address, "@example.com" })
                Assert.DoesNotContain(personal, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(new DateTime(2026, 10, 10, 7, 0, 0, DateTimeKind.Utc), deferred.RemindAt);
        Assert.Equal(deferred.RemindAt, left.RemindAt);
    }

    [Fact]
    public async Task OnboardingPlan_Mutations_RequireOrgAdmin_Test()
    {
        string[] mutations = [nameof(OnboardingPlanController.Defer), nameof(OnboardingPlanController.Exit), nameof(OnboardingPlanController.Resume)];
        var actions = OrgBillingAdminActions.All.Where(a => a.Controller == typeof(OnboardingPlanController)).ToList();

        Assert.Equal(mutations.Order(), actions.Select(a => a.Method.Name).Order());
        Assert.All(actions, action =>
        {
            Assert.False(action.IsAnonymous);
            Assert.Equal([CasazenPolicies.OrgBillingAdmin], action.Policies);
            Assert.Equal("POST", action.HttpMethod);
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.OrgBillingAdmin);
        Assert.NotNull(policy);

        var collaborator = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.None, ["short-rent/bk09_collaborator"]));
        var manager = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.PropertyManager, ["short-rent/property_manager"]));
        var owner = OrgBillingAdminAuthorizationHandlerTests.CreateHandler(
            OrgBillingAdminAuthorizationHandlerTests.Snapshot(UserRole.PropertyOwner, ["short-rent/property_owner"]));

        var asCollaborator = new AuthorizationHandlerContext(policy.Requirements, Principal(), resource: null);
        var asManager = new AuthorizationHandlerContext(policy.Requirements, Principal("PropertyManager"), resource: null);
        var asOwner = new AuthorizationHandlerContext(policy.Requirements, Principal("PropertyOwner"), resource: null);
        await collaborator.HandleAsync(asCollaborator);
        await manager.HandleAsync(asManager);
        await owner.HandleAsync(asOwner);

        Assert.False(asCollaborator.HasSucceeded, "A collaborator of the org passed the policy of defer, exit and resume.");
        Assert.False(asManager.HasSucceeded, "A property manager passed the policy of defer, exit and resume.");
        Assert.True(asOwner.HasSucceeded, "The owner of the org does not pass the policy of defer, exit and resume.");
    }

    [Fact]
    public async Task GetPlan_CountsOnlyThePropertiesTheScopeReaches()
    {
        var service = Service();

        var restricted = await service.GetPlanAsync(_world.OrgId, _world.Restricted, Ct);
        var orgWide = await service.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct);

        var restrictedPrices = Assert.Single(restricted.Parts, p => p.Key == OnboardingPartKey.PricesRules);
        var orgWidePrices = Assert.Single(orgWide.Parts, p => p.Key == OnboardingPartKey.PricesRules);
        Assert.Equal((1, 1), (restrictedPrices.Done, restrictedPrices.Total));
        Assert.Equal((2, 2), (orgWidePrices.Done, orgWidePrices.Total));
        Assert.Equal(OnboardingPartStates.Done, orgWidePrices.State);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetPlanAsync(_world.OtherOrgId, _world.OrgWide, Ct));
    }

    [Fact]
    public async Task GetPlan_EveryPart_AndTheAssistRequests_ReachOnlyWhatTheScopeReaches()
    {
        _db.OctorateConnections.Add(new OctorateConnection
        {
            OrgId = _world.OrgId,
            Environment = OctorateEnvironment.Test,
            Status = OctorateConnectionStatus.Connected,
        });
        _db.OctorateInventoryIntents.AddRange(Intent(_world.Granted.Id, null), Intent(_world.Hidden.Id, null), Intent(null, "lotto-a"));
        _db.OnboardingAssistRequests.AddRange(
            Assist(OnboardingPartKey.Listings, _world.Granted.Id),
            Assist(OnboardingPartKey.Channels, _world.Hidden.Id),
            Assist(OnboardingPartKey.Compliance, null));
        await _db.SaveChangesAsync();
        var service = ScopedService();

        var restricted = await service.GetPlanAsync(_world.OrgId, _world.Restricted, Ct);
        var owned = await service.GetPlanAsync(_world.OrgId, _world.OwnedByOwner, Ct);
        var orgWide = await service.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct);

        static (int Done, int Total) Count(OnboardingPlan plan, OnboardingPartKey key)
        {
            var part = plan.Parts.Single(p => p.Key == key);
            return (part.Done, part.Total);
        }

        Assert.Equal((1, 1), Count(restricted, OnboardingPartKey.OctorateConnection));
        Assert.Equal((2, 2), Count(owned, OnboardingPartKey.OctorateConnection));
        Assert.Equal((3, 3), Count(orgWide, OnboardingPartKey.OctorateConnection));

        foreach (var key in new[] { OnboardingPartKey.Compliance, OnboardingPartKey.Activation })
        {
            Assert.Equal([1, 2, 2], new[] { restricted, owned, orgWide }.Select(plan => Count(plan, key).Total));
            Assert.All(new[] { restricted, owned, orgWide }, plan => Assert.Equal(0, Count(plan, key).Done));
        }

        Assert.Equal(["listings"], AssistedParts(restricted));
        Assert.Equal(["channels", "listings"], AssistedParts(owned));
        Assert.Equal(["channels", "compliance", "listings"], AssistedParts(orgWide));
    }

    [Fact]
    public async Task DeferAndExit_APartThatIsDoneOrNotAvailable_Are422AndWriteNothing()
    {
        var service = Service();

        foreach (var part in new[] { OnboardingPartKey.PricesRules, OnboardingPartKey.MessagesAi })
        {
            var deferred = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.DeferAsync(_world.OrgId, _world.OwnerUserId, part, null, Ct));
            var left = await Assert.ThrowsAsync<DomainRuleException>(() => service.ExitAsync(_world.OrgId, _world.OwnerUserId, part, Ct));
            Assert.Equal(OnboardingPlanCodes.PartNotOpen, deferred.Code);
            Assert.Equal(OnboardingPlanCodes.PartNotOpen, left.Code);
        }

        Assert.Empty(await _db.OnboardingPartStates.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InternalActivities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void PathOf_NextSkipsTheStepsTheHostPutOff_WhichStayVisible()
    {
        var states = OnboardingPartCatalog.Parts.ToDictionary(part => part.Key, _ => OnboardingPartStates.Done);
        states[OnboardingPartKey.Listings] = OnboardingPartStates.Deferred;
        states[OnboardingPartKey.Compliance] = OnboardingPartStates.Open;

        var path = OnboardingPlanService.PathOf(states);

        var listings = Assert.Single(path.Steps, step => step.Code == "checkListings");
        Assert.Equal((OnboardingPartStates.Deferred, true), (listings.State, listings.Visible));
        Assert.Equal("checkInCompliance", path.Next);

        states[OnboardingPartKey.Compliance] = OnboardingPartStates.Done;
        Assert.Null(OnboardingPlanService.PathOf(states).Next);

        states[OnboardingPartKey.Properties] = OnboardingPartStates.NotAvailable;
        var connect = Assert.Single(OnboardingPlanService.PathOf(states).Steps, step => step.Code == "connectChannels");
        Assert.Equal((OnboardingPartStates.Done, false), (connect.State, connect.Visible));
    }

    [Fact]
    public async Task GetPlan_TheTwelvePartsInOrder_WithTheStatesInTheirPrecedence()
    {
        var service = Service();
        await service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Listings, null, Ct);

        var plan = await service.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct);
        OnboardingPlanPart Part(OnboardingPartKey key) => plan.Parts.Single(p => p.Key == key);

        Assert.Equal(OnboardingPartCatalog.Parts.Select(p => p.Code), plan.Parts.Select(p => p.Code));
        Assert.Equal(12, plan.Parts.Count);
        Assert.Equal(
            ("guestRegistration", OnboardingPartStates.NotAvailable, OnboardingPartReasons.NotProvided),
            (Part(OnboardingPartKey.GuestRegistration).Code, Part(OnboardingPartKey.GuestRegistration).State, Part(OnboardingPartKey.GuestRegistration).Reason));
        Assert.Equal(OnboardingNeedCodes.All, plan.Needs);
        Assert.Equal((OnboardingPartStates.NotAvailable, OnboardingPartReasons.NotProvided), (Part(OnboardingPartKey.MessagesAi).State, Part(OnboardingPartKey.MessagesAi).Reason));
        Assert.Equal(OnboardingPartStates.Done, Part(OnboardingPartKey.PricesRules).State);
        Assert.Equal(OnboardingPartStates.Deferred, Part(OnboardingPartKey.Listings).State);
        Assert.NotNull(Part(OnboardingPartKey.Listings).RemindAt);
        Assert.Equal((OnboardingPartStates.Blocked, OnboardingPartReasons.RequiredPartNotDone), (Part(OnboardingPartKey.Channels).State, Part(OnboardingPartKey.Channels).Reason));
        Assert.Equal(["properties"], Part(OnboardingPartKey.Channels).Requires);
        Assert.False(Part(OnboardingPartKey.Channels).Deferrable);
        Assert.True(Part(OnboardingPartKey.Listings).Deferrable);
        Assert.False(plan.Activated);

        Assert.Equal(6, plan.Path.Steps.Count);
        Assert.Equal("connectChannels", plan.Path.Next);
        var prices = Assert.Single(plan.Path.Steps, s => s.Code == "pricesCalendar");
        Assert.False(prices.Visible);
        Assert.Equal(OnboardingPartStates.Done, prices.State);
        Assert.False(Assert.Single(plan.Path.Steps, s => s.Code == "messagesAi").Visible);
    }

    [Fact]
    public async Task Defer_APartAnotherPartRequires_Is422AndWritesNothing()
    {
        var service = Service();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Properties, null, Ct));

        Assert.Equal(OnboardingPlanCodes.PartPrerequisite, refused.Code);
        Assert.Empty(await _db.OnboardingPartStates.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InternalActivities.AsNoTracking().ToListAsync());

        var past = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.PricesRules, HostScopeScenario.Now.UtcDateTime.AddHours(-1), Ct));
        Assert.Equal(OnboardingPlanCodes.RemindAtInvalid, past.Code);
    }

    [Fact]
    public async Task Resume_ClearsTheDeferral_AndClosesItsTodo_AndADeferralAgainRaisesANewOne()
    {
        var service = Service();
        var remindAt = HostScopeScenario.Now.UtcDateTime.AddDays(3);
        var deferred = await service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Listings, remindAt, Ct);

        var resumed = await service.ResumeAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Listings, Ct);

        Assert.Equal((OnboardingPartStates.Open, (DateTime?)null, (Guid?)null), (resumed.State, resumed.RemindAt, resumed.TodoId));
        var closed = await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == deferred.TodoId);
        Assert.NotNull(closed.CompletedAt);
        var row = await _db.OnboardingPartStates.AsNoTracking().SingleAsync();
        Assert.Equal((OnboardingPartStatus.Open, (DateTime?)null, (Guid?)null), (row.Status, row.RemindAt, row.ActivityId));

        var again = await service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Listings, remindAt, Ct);
        Assert.NotEqual(deferred.TodoId, again.TodoId);
        Assert.Equal(1, await _db.InternalActivities.CountAsync(a => a.CompletedAt == null));
    }

    private OnboardingPlanService Service()
    {
        var activities = new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, _clock);
        IOnboardingPartEvaluator[] evaluators =
        [
            new ChannelsPartEvaluator(_db),
            new ListingsPartEvaluator(_db),
            new PricesRulesPartEvaluator(_db),
            new NotProvidedPartEvaluator(OnboardingPartKey.MessagesAi),
            new ConstantEvaluator(OnboardingPartKey.OctorateConnection, new PartFacts(true, 0, 1, OnboardingPartReasons.ConnectionMissing)),
            new ConstantEvaluator(OnboardingPartKey.Properties, new PartFacts(true, 0, 2, OnboardingPartReasons.MappingUnconfirmed)),
        ];
        return new OnboardingPlanService(_db, evaluators, activities, _clock, NullLogger<OnboardingPlanService>.Instance);
    }

    private OnboardingPlanService ScopedService()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        var reader = new FlowPrerequisiteReader(
            _db,
            new StubMappingReader([_world.Granted.Id, _world.Hidden.Id]),
            environment.Object,
            NullLogger<FlowPrerequisiteReader>.Instance);
        var touristTax = new Mock<ITouristTaxQuoteService>();
        touristTax
            .Setup(t => t.GetRatesInForceAsync(It.IsAny<TouristTaxComune>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TouristTaxRate>)[new TouristTaxRate { City = "Ostuni", RatePerPersonPerNight = 2m }]);
        IFlowReadinessProvider[] providers =
        [
            new ListingsFlowProvider(_db, reader),
            new BookingsFlowProvider(reader),
            new CheckInComplianceFlowProvider(_db, reader, touristTax.Object, _clock),
        ];
        IOnboardingPartEvaluator[] evaluators =
        [
            new OctorateConnectionPartEvaluator(_db, reader),
            new CompliancePartEvaluator(providers),
            new ActivationPartEvaluator(new OnboardingFlowStatusService(_db, providers)),
        ];
        var activities = new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, _clock);
        return new OnboardingPlanService(_db, evaluators, activities, _clock, NullLogger<OnboardingPlanService>.Instance);
    }

    private OctorateInventoryIntent Intent(Guid? propertyId, string? group) => new()
    {
        OrgId = _world.OrgId,
        PropertyId = propertyId,
        Scenario = propertyId is null ? OctorateInventoryScenario.NewInventory : OctorateInventoryScenario.OnOctorate,
        WizardBatch = group,
        ActorUserId = _world.OwnerUserId,
    };

    private OnboardingAssistRequest Assist(OnboardingPartKey part, Guid? propertyId) => new()
    {
        OrgId = _world.OrgId,
        PartKey = part,
        PropertyId = propertyId,
        Context = OnboardingAssistContexts.PanelPolice,
        RequestedByUserId = _world.OwnerUserId,
    };

    private static string[] AssistedParts(OnboardingPlan plan) =>
        [.. plan.Parts.Where(part => part.Assist is not null).Select(part => part.Code).Order(StringComparer.Ordinal)];

    private static ClaimsPrincipal Principal(params string[] jwtRoles) =>
        OrgBillingAdminAuthorizationHandlerTests.Context(jwtRoles.Select(role => new Claim(ClaimTypes.Role, role))).User;

    private sealed class StubMappingReader(IReadOnlyCollection<Guid> confirmed) : IPropertyMappingReader
    {
        public bool IsProvided => true;

        public Task<IReadOnlyDictionary<Guid, string>> GetConfirmedAccommodationIdsAsync(
            Guid orgId,
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken ct)
        {
            IReadOnlyDictionary<Guid, string> answer = propertyIds.Where(confirmed.Contains).ToDictionary(id => id, id => $"acc-{id:N}");
            return Task.FromResult(answer);
        }
    }

    private sealed class ConstantEvaluator(OnboardingPartKey part, PartFacts facts) : IOnboardingPartEvaluator
    {
        public OnboardingPartKey Part { get; } = part;

        public int Order => OnboardingPartEvaluatorOrders.Evaluator;

        public Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct) => Task.FromResult(facts);
    }
}
