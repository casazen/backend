using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public sealed class OnboardingTodoReconcilerTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly Mock<IOnboardingPlanService> _plans = new();
    private readonly Mock<IOnboardingFlowStatusService> _flows = new();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync()
    {
        _world = await HostScopeScenario.SeedAsync(_db);
        Plan();
        _flows.Setup(f => f.GetStatusesAsync(It.IsAny<HostScope>(), null, It.IsAny<IReadOnlyCollection<OnboardingFlow>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FlowPropertyStatus>());
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task CompletedPart_ClearsAllEarlierAttemptsImmediately_WithoutConnection_AndKeepsAudit()
    {
        var part = new OnboardingPartState { OrgId = _world.OrgId, PartKey = OnboardingPartKey.Compliance };
        var step = new OnboardingPanelStep { OrgId = _world.OrgId, Kind = PanelStepKind.PoliceConnection };
        var assist = Assist(_world.OrgId, OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice);
        _db.AddRange(part, step, assist);
        var reminders = new[]
        {
            Todo(_world.OrgId, OnboardingPlanCodes.TodoEntityType, part.Id),
            Todo(_world.OrgId, OnboardingPanelStepCodes.TodoEntityType, step.Id),
            Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, assist.Id),
        };
        _db.AddRange(reminders);
        await _db.SaveChangesAsync();
        Plan(OnboardingPartKey.Compliance);

        await Reconciler().ReconcileAsync(_world.OrgWide, CancellationToken.None);
        await Reconciler().ReconcileAsync(_world.OrgWide, CancellationToken.None);

        foreach (var reminder in reminders)
        {
            var closed = await _db.InternalActivities.SingleAsync(a => a.Id == reminder.Id);
            Assert.Equal(InternalActivity.StatusCompleted, closed.Status);
            Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime, closed.CompletedAt);
        }
        Assert.Empty(await Activities().GetPendingAsync(_world.OrgWide, CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedAttempts_ShowOneReminderPerProcess_AndKeepOtherChannelsAndProperties()
    {
        var first = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, _world.Granted.Id, "booking");
        var retry = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, _world.Granted.Id, "booking");
        var otherChannel = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, _world.Granted.Id, "airbnb");
        var otherProperty = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, _world.Hidden.Id, "booking");
        _db.AddRange(first, retry, otherChannel, otherProperty);
        var old = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, first.Id, _world.Granted.Id);
        var latest = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, retry.Id, _world.Granted.Id);
        latest.CreatedAt = old.CreatedAt.AddMinutes(1);
        var channelTodo = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, otherChannel.Id, _world.Granted.Id);
        var propertyTodo = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, otherProperty.Id, _world.Hidden.Id);
        _db.AddRange(old, latest, channelTodo, propertyTodo);
        await _db.SaveChangesAsync();

        await Reconciler().ReconcileAsync(_world.OrgWide, CancellationToken.None);

        var pending = await Activities().GetPendingAsync(_world.OrgWide, CancellationToken.None);
        Assert.Equal(new[] { latest.Id, channelTodo.Id, propertyTodo.Id }.Order(), pending.Select(a => a.Id).Order());
        Assert.Equal(InternalActivity.ClosedBySystem, (await _db.InternalActivities.SingleAsync(a => a.Id == old.Id)).ClosedBy);
    }

    [Fact]
    public async Task Reconciliation_NeverClosesAnotherTenantOrAnUnreachableProperty()
    {
        var own = Assist(_world.OrgId, OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.Granted.Id);
        var hidden = Assist(_world.OrgId, OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.Hidden.Id);
        var foreign = Assist(_world.OtherOrgId, OnboardingPartKey.Compliance, OnboardingAssistContexts.PanelPolice, _world.OtherOrgProperty.Id);
        _db.AddRange(own, hidden, foreign);
        var ownTodo = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, own.Id, own.PropertyId);
        var hiddenTodo = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, hidden.Id, hidden.PropertyId);
        var foreignTodo = Todo(_world.OtherOrgId, OnboardingAssistCodes.TodoEntityType, foreign.Id, foreign.PropertyId);
        _db.AddRange(ownTodo, hiddenTodo, foreignTodo);
        await _db.SaveChangesAsync();
        Plan(OnboardingPartKey.Compliance);

        await Reconciler().ReconcileAsync(_world.Restricted, CancellationToken.None);

        Assert.NotNull((await _db.InternalActivities.SingleAsync(a => a.Id == ownTodo.Id)).CompletedAt);
        Assert.Null((await _db.InternalActivities.SingleAsync(a => a.Id == hiddenTodo.Id)).CompletedAt);
        Assert.Null((await _db.InternalActivities.SingleAsync(a => a.Id == foreignTodo.Id)).CompletedAt);
        _plans.Verify(p => p.GetPlanAsync(_world.OrgId, It.Is<HostScope>(s => s.IsOrgWide), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SettledStepsAndRequestsAndActiveFlows_ClearReminders_EvenWhenPartIsStillOpen()
    {
        var step = new OnboardingPanelStep { OrgId = _world.OrgId, Kind = PanelStepKind.PoliceConnection, Status = PanelStepStatus.Verified };
        var assist = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess);
        assist.Status = OnboardingAssistStatus.Done;
        _db.AddRange(step, assist);
        _db.AddRange(
            Todo(_world.OrgId, OnboardingPanelStepCodes.TodoEntityType, step.Id),
            Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, assist.Id),
            Todo(_world.OrgId, OnboardingActivationCodes.FlowTodoEntityType, OnboardingFlowTodo.EntityIdOf(_world.OrgId, OnboardingFlow.Listings)));
        await _db.SaveChangesAsync();

        await Reconciler().ReconcileAsync(_world.OrgWide, CancellationToken.None);

        Assert.Empty(await Activities().GetPendingAsync(_world.OrgWide, CancellationToken.None));
    }

    [Fact]
    public async Task CompletedRetry_ClearsTheEarlierAttempt_ButDoesNotHideANewerReopenedProcess()
    {
        var earlier = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, "booking");
        var done = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, "booking");
        done.Status = OnboardingAssistStatus.Done;
        var reopened = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, "airbnb");
        var oldDone = Assist(_world.OrgId, OnboardingPartKey.Channels, OnboardingAssistContexts.PanelChannelAccess, null, "airbnb");
        oldDone.Status = OnboardingAssistStatus.Done;
        _db.AddRange(earlier, done, reopened, oldDone);
        var earlierTodo = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, earlier.Id);
        var doneTodo = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, done.Id);
        doneTodo.CreatedAt = earlierTodo.CreatedAt.AddMinutes(1);
        var reopenedTodo = Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, reopened.Id);
        reopenedTodo.CreatedAt = earlierTodo.CreatedAt.AddMinutes(2);
        _db.AddRange(earlierTodo, doneTodo, reopenedTodo, Todo(_world.OrgId, OnboardingAssistCodes.TodoEntityType, oldDone.Id));
        await _db.SaveChangesAsync();

        await Reconciler().ReconcileAsync(_world.OrgWide, CancellationToken.None);

        Assert.Equal(reopenedTodo.Id, Assert.Single(await Activities().GetPendingAsync(_world.OrgWide, CancellationToken.None)).Id);
    }

    private InternalActivityService Activities() => new(_db, NullLogger<InternalActivityService>.Instance, _clock);
    private OnboardingTodoReconciler Reconciler() => new(_db, _plans.Object, _flows.Object, Activities());

    private void Plan(params OnboardingPartKey[] completed)
    {
        var parts = OnboardingPartCatalog.Parts.Select(p => new OnboardingPlanPart(p.Key, p.Code, p.Order,
            completed.Contains(p.Key) ? OnboardingPartStates.Done : OnboardingPartStates.Open,
            null, 0, 1, [], true, [], null, null, p.LaunchTarget)).ToList();
        _plans.Setup(p => p.GetPlanAsync(_world.OrgId, It.IsAny<HostScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OnboardingPlan(parts, [], new OnboardingPath("test", [], null), false));
    }

    private static OnboardingAssistRequest Assist(Guid orgId, OnboardingPartKey part, string context, Guid? propertyId = null, string? channel = null) =>
        new() { OrgId = orgId, PartKey = part, Context = context, PropertyId = propertyId, ChannelCode = channel };

    private InternalActivity Todo(Guid orgId, string entityType, Guid entityId, Guid? propertyId = null) => new()
    {
        OrgId = orgId, PropertyId = propertyId, ActivityType = nameof(ActivityType.WizardResume), Action = ActivityAction.ResumeWizard,
        EntityType = entityType, EntityId = entityId, CreatedAt = _clock.GetUtcNow().UtcDateTime, ScheduledAt = _clock.GetUtcNow().UtcDateTime,
        Urgency = ActivityUrgency.Medium,
    };
}
