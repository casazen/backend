using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The activation of the flows, the final read-only test and the "To do" sync (E10/F01/US01/T04, NF-104, NF-106, NF-108): the proofs of the
/// permission rule of the activation (ONB-05 «Prova») and of the data rule of the final test, with the real flow status of T02 on EF InMemory and the
/// world of <see cref="HostScopeScenario"/> (an org with two properties, the collaborator «Solo alcuni» given the first, another org).
/// </summary>
public class OnboardingFlowActivationTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly SaveCounter _saves = new();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private AppDbContext _db = null!;
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync()
    {
        _db = NewDb();
        _world = await HostScopeScenario.SeedAsync(_db);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>
    /// Proof (permission, «Prova» of ONB-05): a flow is never activated, and no row nor event is written, while the connection, the confirmed mapping
    /// or the single sender does not hold: the outcome lists what does not hold. One call mixes the outcomes: the property whose prerequisites hold is
    /// activated (a row and its event in the same save), a property of another org and an id that does not exist are <c>notFound</c>, and the
    /// collaborator «Solo alcuni» cannot reach the property it was not given.
    /// </summary>
    [Fact]
    public async Task FlowActivation_PrerequisitesUnmet_NeverActivated_Test()
    {
        // No connection at all: the connection does not hold, nothing is written.
        var noConnection = await Activation().ActivateAsync(_world.OrgWide, _world.OwnerUserId, OnboardingFlow.Bookings, [_world.Granted.Id], Ct);
        var refused = Assert.Single(noConnection);
        Assert.Equal(FlowActivationOutcomes.PrerequisitesNotMet, refused.Outcome);
        Assert.Contains(OnboardingPrerequisiteKeys.Connection, refused.UnmetPrerequisites);
        await AssertNothingWrittenAsync();

        // Connected; the listing of Granted is confirmed, the one of Hidden is not.
        await ConnectAsync(OctorateConnectionStatus.Connected);
        var unknown = Guid.NewGuid();
        var mixed = await Activation(_world.Granted.Id).ActivateAsync(
            _world.OrgWide,
            _world.OwnerUserId,
            OnboardingFlow.Bookings,
            [_world.Granted.Id, _world.Hidden.Id, _world.OtherOrgProperty.Id, unknown],
            Ct);

        Assert.Equal([_world.Granted.Id, _world.Hidden.Id, _world.OtherOrgProperty.Id, unknown], mixed.Select(o => o.PropertyId));
        Assert.Equal(FlowActivationOutcomes.Activated, mixed[0].Outcome);
        Assert.Equal(FlowActivationOutcomes.PrerequisitesNotMet, mixed[1].Outcome);
        Assert.Equal([OnboardingPrerequisiteKeys.Mapping], mixed[1].UnmetPrerequisites);
        Assert.Equal(OnboardingFlowReasons.MappingUnconfirmed, mixed[1].Reason);
        Assert.Equal(FlowActivationOutcomes.NotFound, mixed[2].Outcome);
        Assert.Equal(FlowActivationOutcomes.NotFound, mixed[3].Outcome);

        var rows = await _db.PropertyFlowActivations.AsNoTracking().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal((_world.OrgId, _world.Granted.Id, OnboardingFlow.Bookings, (DateTime?)null), (row.OrgId, row.PropertyId, row.Flow, row.DeactivatedAt));
        var activated = Assert.Single(await _db.OutboxEvents.AsNoTracking().ToListAsync());
        Assert.Equal((DomainEventType.OnboardingFlowActivated, _world.Granted.Id, _world.OrgId), (activated.EventType, activated.EntityId, activated.OrgId));
        Assert.Contains(row.Id.ToString(), activated.PayloadJson, StringComparison.Ordinal);

        // The check-in and compliance flow also needs a single stored sender: Granted has none, so it is refused and nothing more is written.
        var compliance = await Activation(_world.Granted.Id).ActivateAsync(
            _world.OrgWide, _world.OwnerUserId, OnboardingFlow.CheckInCompliance, [_world.Granted.Id], Ct);
        Assert.Equal(FlowActivationOutcomes.PrerequisitesNotMet, Assert.Single(compliance).Outcome);
        Assert.Contains(OnboardingPrerequisiteKeys.SingleSender, compliance[0].UnmetPrerequisites);
        Assert.Single(await _db.PropertyFlowActivations.AsNoTracking().ToListAsync());
        Assert.Single(await _db.OutboxEvents.AsNoTracking().ToListAsync());

        // The collaborator «Solo alcuni» reaches Granted only: Hidden is notFound for it, even with every prerequisite met.
        var restricted = await Activation([_world.Granted.Id, _world.Hidden.Id]).ActivateAsync(
            _world.Restricted, _world.CollaboratorId, OnboardingFlow.Bookings, [_world.Hidden.Id], Ct);
        Assert.Equal(FlowActivationOutcomes.NotFound, Assert.Single(restricted).Outcome);
        Assert.Single(await _db.PropertyFlowActivations.AsNoTracking().ToListAsync());

        // An empty list never means «all».
        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => Activation().ActivateAsync(_world.OrgWide, _world.OwnerUserId, OnboardingFlow.Bookings, [], Ct));
        Assert.Equal(OnboardingActivationCodes.PropertyIdsEmpty, empty.Code);
    }

    /// <summary>
    /// Proof (data): the final test adds, changes and deletes nothing and records no outbox event: no save of the unit of work, the same rows in
    /// the tables before and after, nothing pending at the end. It answers one check per flow on the most recent imported booking.
    /// </summary>
    [Fact]
    public async Task FlowTestRun_ReadOnly_NoSideEffects_Test()
    {
        var older = await SeedImportedBookingAsync(_world.Granted, HostScopeScenario.Now.UtcDateTime.AddDays(-3), otaCommission: null);
        var latest = await SeedImportedBookingAsync(_world.Granted, HostScopeScenario.Now.UtcDateTime.AddHours(-1), otaCommission: 18m);
        _db.ListingChannelStates.Add(new ListingChannelState
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Granted.Id,
            ChannelCode = "booking_xml",
            Status = ChannelPublishStatus.Published,
        });
        await _db.SaveChangesAsync();

        await using var db = NewDb();
        var counts = await CountsAsync(db);
        var saves = _saves.Count;
        var wizard = new Mock<ICheckInWizardService>(MockBehavior.Strict);
        wizard.Setup(w => w.GetStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid bookingId, CancellationToken _) =>
                WizardState(bookingId, _world.Granted.Id, _world.OrgId, SenderSource.Stored, guestDataMissing: true));
        var service = new OnboardingFlowTestRunService(
            db,
            [new ListingsDryRunCheck(db), new BookingsDryRunCheck(new BookingStateMapper()), new CheckInComplianceDryRunCheck(wizard.Object)],
            NullLogger<OnboardingFlowTestRunService>.Instance);

        var result = await service.RunAsync(_world.OrgWide, _world.Granted.Id, null, Ct);

        Assert.Equal(latest, result.BookingId);
        Assert.Equal(Enum.GetValues<OnboardingFlow>().OrderBy(f => (int)f), result.Checks.Select(c => c.Flow));
        Assert.Equal(FlowCheckOutcomes.Pass, Check(result, OnboardingFlow.Listings).Outcome);
        Assert.Equal(FlowCheckOutcomes.Pass, Check(result, OnboardingFlow.Bookings).Outcome);
        Assert.Equal((FlowCheckOutcomes.Pass, FlowCheckReasons.GuestDataMissing), (Check(result, OnboardingFlow.CheckInCompliance).Outcome, Assert.Single(Check(result, OnboardingFlow.CheckInCompliance).Reasons)));
        Assert.All(
            new[] { OnboardingFlow.Messages, OnboardingFlow.Documents, OnboardingFlow.Reports },
            flow => Assert.Equal((FlowCheckOutcomes.Skipped, FlowCheckReasons.NotProvided), (Check(result, flow).Outcome, Check(result, flow).Reasons.Single())));

        // The older booking of an OTA without any documented amount fails the bookings check: still nothing written.
        var onOlder = await service.RunAsync(_world.OrgWide, _world.Granted.Id, older, Ct);
        Assert.Equal((FlowCheckOutcomes.Fail, FlowCheckReasons.OtaAmountsMissing), (Check(onOlder, OnboardingFlow.Bookings).Outcome, Check(onOlder, OnboardingFlow.Bookings).Reasons.Single()));

        // No save, nothing pending, the same rows everywhere and no outbox event.
        Assert.Equal(saves, _saves.Count);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(counts, await CountsAsync(db));
        Assert.Empty(await db.OutboxEvents.AsNoTracking().ToListAsync());
        wizard.Verify(w => w.GetStateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        wizard.VerifyNoOtherCalls();

        // A property with no imported booking, and a property of another org: not found.
        var none = await Assert.ThrowsAsync<NotFoundException>(() => service.RunAsync(_world.OrgWide, _world.Hidden.Id, null, Ct));
        Assert.Equal(OnboardingActivationCodes.TestBookingNotFound, none.Code);
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() => service.RunAsync(_world.OrgWide, _world.OtherOrgProperty.Id, null, Ct));
        Assert.Equal(OnboardingActivationCodes.PropertyNotFound, foreign.Code);
    }

    /// <summary>
    /// AC-NF104-12, AC-NF106-1: a repeated activation answers <c>alreadyActive</c> and writes nothing; an activated flow stops counting as active
    /// while a prerequisite does not hold (the row stays), and is active again when it holds; a deactivation sets <c>DeactivatedAt</c> and records
    /// its event, and a reactivation reuses the row.
    /// </summary>
    [Fact]
    public async Task Activation_Repeated_Lost_Deactivated_Reactivated()
    {
        await ConnectAsync(OctorateConnectionStatus.Connected);
        await Activation(_world.Granted.Id).ActivateAsync(_world.OrgWide, _world.OwnerUserId, OnboardingFlow.Bookings, [_world.Granted.Id], Ct);

        var again = await Activation(_world.Granted.Id).ActivateAsync(
            _world.OrgWide, _world.OwnerUserId, OnboardingFlow.Bookings, [_world.Granted.Id], Ct);
        Assert.Equal(FlowActivationOutcomes.AlreadyActive, Assert.Single(again).Outcome);
        Assert.Single(await _db.OutboxEvents.AsNoTracking().ToListAsync());
        Assert.True(await Reader(_world.Granted.Id).IsActiveAsync(_world.Granted.Id, OnboardingFlow.Bookings, Ct));

        // The connection is lost: the flow is no longer active, its row stays as it was.
        var connection = await _db.OctorateConnections.SingleAsync();
        connection.Status = OctorateConnectionStatus.Error;
        await _db.SaveChangesAsync();
        Assert.False(await Reader(_world.Granted.Id).IsActiveAsync(_world.Granted.Id, OnboardingFlow.Bookings, Ct));
        Assert.Null((await _db.PropertyFlowActivations.AsNoTracking().SingleAsync()).DeactivatedAt);
        connection.Status = OctorateConnectionStatus.Connected;
        await _db.SaveChangesAsync();
        Assert.True(await Reader(_world.Granted.Id).IsActiveAsync(_world.Granted.Id, OnboardingFlow.Bookings, Ct));

        var off = await Activation().DeactivateAsync(_world.OrgWide, _world.OwnerUserId, OnboardingFlow.Bookings, [_world.Granted.Id, _world.Hidden.Id], Ct);
        Assert.Equal([FlowActivationOutcomes.Deactivated, FlowActivationOutcomes.AlreadyInactive], off.Select(o => o.Outcome));
        var row = await _db.PropertyFlowActivations.AsNoTracking().SingleAsync();
        Assert.Equal(HostScopeScenario.Now.UtcDateTime, row.DeactivatedAt);
        Assert.Equal(_world.OwnerUserId, row.DeactivatedByUserId);
        Assert.Contains(await _db.OutboxEvents.AsNoTracking().ToListAsync(), e => e.EventType == DomainEventType.OnboardingFlowDeactivated);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var back = await Activation(_world.Granted.Id).ActivateAsync(
            _world.OrgWide, _world.OwnerUserId, OnboardingFlow.Bookings, [_world.Granted.Id], Ct);
        Assert.Equal(FlowActivationOutcomes.Activated, Assert.Single(back).Outcome);
        var reused = await _db.PropertyFlowActivations.AsNoTracking().SingleAsync();
        Assert.Equal((row.Id, (DateTime?)null), (reused.Id, reused.DeactivatedAt));
        Assert.Equal(3, await _db.OutboxEvents.CountAsync());
    }

    /// <summary>
    /// AC-NF106-3, AC-NF108-4: the sync keeps ONE "To do" per flow with properties to complete and ONE high-urgency reconnect "To do" while the
    /// connection is lost, raises nothing twice, and closes them when the condition clears. Codes and ids only.
    /// </summary>
    [Fact]
    public async Task TodoSync_KeepsOneTodoPerIncompleteFlow_AndTheReconnectOne_AndClosesThemWhenCleared()
    {
        await ConnectAsync(OctorateConnectionStatus.Disconnected);
        var statuses = new StubFlowStatus();
        statuses.Set(_world.OrgId, OnboardingFlow.Bookings, OnboardingFlowStates.ToComplete);
        var job = Job(statuses, new TestFeatureFlags(FeatureFlags.OctorateOnboarding));

        await job.ExecuteAsync(Ct);
        await job.ExecuteAsync(Ct);

        var open = await OpenTodosAsync();
        Assert.Equal(2, open.Count);
        var flowTodo = Assert.Single(open, a => a.ActivityType == nameof(ActivityType.WizardResume));
        Assert.Equal(
            (OnboardingActivationCodes.FlowTodoEntityType, OnboardingFlowTodo.EntityIdOf(_world.OrgId, OnboardingFlow.Bookings), OnboardingActivationCodes.ReasonFlowIncomplete),
            (flowTodo.EntityType, flowTodo.EntityId, flowTodo.Reason));
        var reconnect = Assert.Single(open, a => a.ActivityType == nameof(ActivityType.ReconnectChannelManager));
        Assert.Equal((ActivityUrgency.High, ActivityAction.ReconnectChannel), (reconnect.Urgency, reconnect.Action));
        Assert.All(open, a => Assert.True(a.PropertyId is null && a.StaffUserId is null && a.Notes is null));

        // Connected again and nothing to complete: both close; the closed rows stay.
        var connection = await _db.OctorateConnections.SingleAsync();
        connection.Status = OctorateConnectionStatus.Connected;
        await _db.SaveChangesAsync();
        statuses.Set(_world.OrgId, OnboardingFlow.Bookings, OnboardingFlowStates.Active);
        await job.ExecuteAsync(Ct);

        Assert.Empty(await OpenTodosAsync());
        Assert.Equal(2, await _db.InternalActivities.CountAsync(a => a.CompletedAt != null));

        // With the flag off a run writes nothing.
        statuses.Set(_world.OrgId, OnboardingFlow.Bookings, OnboardingFlowStates.ToComplete);
        await Job(statuses, new TestFeatureFlags()).ExecuteAsync(Ct);
        Assert.Empty(await OpenTodosAsync());
    }

    [Fact]
    public void TodoSyncJob_IsScheduledHourlyAtMinute20Utc_OnlyWithTheFlagOn_AndHoldsItsLock()
    {
        var (on, onJobs) = Manager();
        RecurringJobsRegistration.Configure(on.Object, new TestFeatureFlags(FeatureFlags.OctorateOnboarding));
        var scheduled = onJobs[OnboardingFlowTodoSyncJob.RecurringJobId];
        Assert.Equal(("onboarding-flow-todo-sync", "20 * * * *"), (OnboardingFlowTodoSyncJob.RecurringJobId, scheduled.Cron));
        Assert.Equal(TimeZoneInfo.Utc, scheduled.Options.TimeZone);
        var attribute = scheduled.Job.Method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(attribute);
        Assert.InRange(attribute.TimeoutSec, 1, (int)HangfireStorageSettings.DefaultDistributedLockTimeout.TotalSeconds);
        on.Verify(m => m.RemoveIfExists(OnboardingFlowTodoSyncJob.RecurringJobId), Times.Never);

        var (off, offJobs) = Manager();
        RecurringJobsRegistration.Configure(off.Object, new TestFeatureFlags());
        Assert.DoesNotContain(OnboardingFlowTodoSyncJob.RecurringJobId, offJobs.Keys);
        off.Verify(m => m.RemoveIfExists(OnboardingFlowTodoSyncJob.RecurringJobId), Times.Once);
    }

    [Fact]
    public void FlowTodoEntityId_IsTheFirst16BytesOfTheSha256OfTheOrgAndTheFlow()
    {
        var orgId = Guid.Parse("6c1f2f0e-8f7e-4f42-9c1d-0d6b2f3e4a5b");
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"onboarding-flow:{orgId}:Bookings"));

        Assert.Equal(new Guid(hash.AsSpan(0, 16)), OnboardingFlowTodo.EntityIdOf(orgId, OnboardingFlow.Bookings));
        Assert.NotEqual(OnboardingFlowTodo.EntityIdOf(orgId, OnboardingFlow.Bookings), OnboardingFlowTodo.EntityIdOf(orgId, OnboardingFlow.Listings));
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).AddInterceptors(_saves).Options);

    private OnboardingFlowActivationService Activation(params Guid[] confirmed) =>
        new(_db, FlowStatus(confirmed), new DomainEventRecorder(_db, NullTenantContext.Instance, _clock), _clock, NullLogger<OnboardingFlowActivationService>.Instance);

    private FlowActivationReader Reader(params Guid[] confirmed) => new(_db, FlowStatus(confirmed));

    /// <summary>The real flow status of T02, with the listings of <paramref name="confirmed"/> confirmed and a tourist tax rate in force everywhere.</summary>
    private OnboardingFlowStatusService FlowStatus(Guid[] confirmed)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        var reader = new FlowPrerequisiteReader(_db, new StubMappingReader(confirmed), environment.Object, NullLogger<FlowPrerequisiteReader>.Instance);
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
        return new OnboardingFlowStatusService(_db, providers);
    }

    private OnboardingFlowTodoSyncJob Job(IOnboardingFlowStatusService statuses, IFeatureFlags flags)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        return new OnboardingFlowTodoSyncJob(
            _db,
            statuses,
            new InternalActivityService(_db, NullLogger<InternalActivityService>.Instance, _clock),
            flags,
            environment.Object,
            NullLogger<OnboardingFlowTodoSyncJob>.Instance);
    }

    private async Task ConnectAsync(OctorateConnectionStatus status)
    {
        _db.OctorateConnections.Add(new OctorateConnection { OrgId = _world.OrgId, Environment = OctorateEnvironment.Test, Status = status });
        await _db.SaveChangesAsync();
    }

    private async Task<Guid> SeedImportedBookingAsync(Property property, DateTime createdAt, decimal? otaCommission)
    {
        var guest = new Guest { OrgId = property.OrgId, FirstName = "Ospite", LastName = "Prova", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, 20),
            CheckOutDate = HostScopeScenario.Day(2026, 10, 22),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Airbnb,
            OctorateReservationId = $"R{Guid.NewGuid():N}"[..20],
            OtaCommission = otaCommission,
            BasePrice = 200m,
            TotalPrice = 200m,
            CreatedAt = createdAt,
        };
        _db.Guests.Add(guest);
        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task AssertNothingWrittenAsync()
    {
        Assert.Empty(await _db.PropertyFlowActivations.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.OutboxEvents.AsNoTracking().ToListAsync());
    }

    private Task<List<InternalActivity>> OpenTodosAsync() =>
        _db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.CompletedAt == null).ToListAsync();

    private static async Task<(int Bookings, int Properties, int Activations, int States, int Todos, int Events, int Guests)> CountsAsync(AppDbContext db) =>
        (await db.Bookings.CountAsync(),
            await db.Properties.CountAsync(),
            await db.PropertyFlowActivations.CountAsync(),
            await db.ListingChannelStates.CountAsync(),
            await db.InternalActivities.CountAsync(),
            await db.OutboxEvents.CountAsync(),
            await db.Guests.CountAsync());

    private static FlowCheckResult Check(FlowTestRunResult result, OnboardingFlow flow) => result.Checks.Single(c => c.Flow == flow);

    private static CheckInState WizardState(Guid bookingId, Guid propertyId, Guid orgId, SenderSource source, bool guestDataMissing) => new(
        new CheckInBookingView(
            bookingId, propertyId, "Trullo", new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22), 2, BookingSource.Airbnb, 2, BookingStatus.Confirmed, null),
        StayAttendance.Expected,
        [],
        [],
        guestDataMissing ? new[] { new CheckInIssue(0, CheckInIssueCodes.GuestFieldMissing, "birthPlace", true) } : Array.Empty<CheckInIssue>(),
        null,
        new PropertySenderInfo(propertyId, orgId, ComplianceSenderType.Manual, ComplianceSenderType.Manual, source, true, null, null),
        new OctorateCheckInView(false, [], []),
        new AlloggiatiStatusInfo(bookingId, AlloggiatiWebStatus.DaInviare, null, null, null, new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), false, 24, false, false),
        new CheckInCapabilities(false, false, false, false),
        CheckInWizardSteps.Guests);

    private static (Mock<IRecurringJobManager> Manager, Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Jobs) Manager()
    {
        var jobs = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => jobs[id] = (job, cron, options));
        return (manager, jobs);
    }

    /// <summary>The confirmed mapping of the properties named, and no other.</summary>
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

    /// <summary>A flow status that answers the state the test sets for each org and flow, on one property.</summary>
    private sealed class StubFlowStatus : IOnboardingFlowStatusService
    {
        private readonly Dictionary<(Guid OrgId, OnboardingFlow Flow), string> _states = [];

        public void Set(Guid orgId, OnboardingFlow flow, string state) => _states[(orgId, flow)] = state;

        public Task<IReadOnlyList<FlowSummary>> GetSummariesAsync(HostScope scope, CancellationToken ct) => throw new NotSupportedException();

        public Task<FlowPropertyPage> GetPropertiesAsync(
            HostScope scope,
            OnboardingFlow flow,
            string? state,
            string? search,
            int page,
            int pageSize,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<FlowPropertyStatus>> GetStatusesAsync(
            HostScope scope,
            IReadOnlyCollection<Guid>? propertyIds,
            IReadOnlyCollection<OnboardingFlow> flows,
            CancellationToken ct)
        {
            var property = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
            IReadOnlyList<FlowPropertyStatus> statuses =
            [
                .. flows.Select(flow => new FlowPropertyStatus(
                    property,
                    "Casa",
                    "Ostuni",
                    flow,
                    _states.TryGetValue((scope.OrgId, flow), out var state) ? state : OnboardingFlowStates.NotAvailable,
                    null,
                    null,
                    [])),
            ];
            return Task.FromResult(statuses);
        }
    }

    /// <summary>Counts the saves of the unit of work.</summary>
    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Count++;
            return base.SavingChanges(eventData, result);
        }
    }
}
