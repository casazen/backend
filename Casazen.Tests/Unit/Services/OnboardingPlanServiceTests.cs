using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The plan of the activation (E10/F01/US01/T03, NF-104, NF-10): the proofs of its privacy and permission rules, and the rules of the states,
/// the path and the deferral, on EF InMemory and the world of <see cref="HostScopeScenario"/> (an org with a property given to the collaborator
/// «Solo alcuni», one not given, and another org).
/// </summary>
public class OnboardingPlanServiceTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>
    /// Proof (privacy, ADR-026-R3): the "To do" a deferral and an exit raise holds codes and ids only: the type, the action, the entity
    /// <c>OnboardingPart</c> with the id of the stored part, a reason code and a deadline. No property, no booking, no person it is for, no
    /// note, no creator, and nothing of the user, the org or its properties in any text column.
    /// </summary>
    [Fact]
    public async Task OnboardingTodo_CarriesNoPersonalData_Test()
    {
        var service = Service();

        var deferred = await service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.PricesRules, null, Ct);
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

        // Tomorrow at 09:00 in Rome (07:00 UTC on 10 October, summer time), the default reminder of both.
        Assert.Equal(new DateTime(2026, 10, 10, 7, 0, 0, DateTimeKind.Utc), deferred.RemindAt);
        Assert.Equal(deferred.RemindAt, left.RemindAt);
    }

    /// <summary>
    /// Proof (permission): defer, exit and resume are under the org policy <see cref="CasazenPolicies.OrgBillingAdmin"/> and nothing else
    /// opens them: no <c>[AllowAnonymous]</c>, and the real registered policy refuses a collaborator of the org (a DB membership, no Staff
    /// claim) and a property manager, while the owner passes (the positive control).
    /// </summary>
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

    /// <summary>
    /// The reads count only what the caller reaches (AM-03): the collaborator «Solo alcuni» counts its one property, the owner the two of the
    /// org, and the property of the other org is never counted.
    /// </summary>
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
    public async Task GetPlan_TheElevenPartsInOrder_WithTheStatesInTheirPrecedence()
    {
        var service = Service();
        await service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.Listings, null, Ct);

        var plan = await service.GetPlanAsync(_world.OrgId, _world.OrgWide, Ct);
        OnboardingPlanPart Part(OnboardingPartKey key) => plan.Parts.Single(p => p.Key == key);

        Assert.Equal(OnboardingPartCatalog.Parts.Select(p => p.Code), plan.Parts.Select(p => p.Code));
        Assert.Equal(11, plan.Parts.Count);
        Assert.Equal(OnboardingNeedCodes.All, plan.Needs);
        // No evaluator: never presumed done.
        Assert.Equal((OnboardingPartStates.NotAvailable, OnboardingPartReasons.NotProvided), (Part(OnboardingPartKey.MessagesAi).State, Part(OnboardingPartKey.MessagesAi).Reason));
        // Counted done from the stored facts (both properties have a nightly price): done wins over everything but «not available».
        Assert.Equal(OnboardingPartStates.Done, Part(OnboardingPartKey.PricesRules).State);
        // Not done and stored deferred: deferred, even though the part it requires is not done.
        Assert.Equal(OnboardingPartStates.Deferred, Part(OnboardingPartKey.Listings).State);
        Assert.NotNull(Part(OnboardingPartKey.Listings).RemindAt);
        // Not done, not deferred, and the part it requires is not done: blocked.
        Assert.Equal((OnboardingPartStates.Blocked, OnboardingPartReasons.RequiredPartNotDone), (Part(OnboardingPartKey.Channels).State, Part(OnboardingPartKey.Channels).Reason));
        Assert.Equal(["properties"], Part(OnboardingPartKey.Channels).Requires);
        Assert.False(Part(OnboardingPartKey.Channels).Deferrable);
        Assert.True(Part(OnboardingPartKey.Listings).Deferrable);
        Assert.False(plan.Activated);

        // The path: the first visible step is the next one; a step whose parts are all done is not visible.
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
        var deferred = await service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.PricesRules, remindAt, Ct);

        var resumed = await service.ResumeAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.PricesRules, Ct);

        Assert.Equal((OnboardingPartStates.Open, (DateTime?)null, (Guid?)null), (resumed.State, resumed.RemindAt, resumed.TodoId));
        var closed = await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == deferred.TodoId);
        Assert.NotNull(closed.CompletedAt);
        var row = await _db.OnboardingPartStates.AsNoTracking().SingleAsync();
        Assert.Equal((OnboardingPartStatus.Open, (DateTime?)null, (Guid?)null), (row.Status, row.RemindAt, row.ActivityId));

        var again = await service.DeferAsync(_world.OrgId, _world.OwnerUserId, OnboardingPartKey.PricesRules, remindAt, Ct);
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

    private static ClaimsPrincipal Principal(params string[] jwtRoles) =>
        OrgBillingAdminAuthorizationHandlerTests.Context(jwtRoles.Select(role => new Claim(ClaimTypes.Role, role))).User;

    /// <summary>An evaluator that always answers the same facts.</summary>
    private sealed class ConstantEvaluator(OnboardingPartKey part, PartFacts facts) : IOnboardingPartEvaluator
    {
        public OnboardingPartKey Part { get; } = part;

        public int Order => OnboardingPartEvaluatorOrders.Evaluator;

        public Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct) => Task.FromResult(facts);
    }
}
