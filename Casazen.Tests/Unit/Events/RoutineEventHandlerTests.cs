using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Events;

/// <summary>
/// NF-21, ADR-022-R5, R7, R8: <see cref="RoutineEventHandler"/> connects the routine engine to the bus. An event of a mappable type
/// runs the matching routines of its org once, however many times it is delivered (in parallel too, proved on PostgreSQL); an
/// unmappable type runs nothing; a time trigger runs only the routines with its offset; a failing routine is recorded and never fails
/// the event.
/// </summary>
public class RoutineEventHandlerTests
{
    private static readonly Guid OrgA = Guid.Parse("0f6c1a52-3b7e-4c11-9d2a-5e8b7c6d4a01");
    private static readonly Guid OrgB = Guid.Parse("8e2d4b19-6a5f-4f3c-b1e7-2c9a0d3e5f02");
    private static readonly DateTime Now = new(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc);

    private readonly string _database = $"routine-handler-{Guid.NewGuid()}";

    [Fact]
    public async Task HandleAsync_MappableEventDeliveredTwice_RunsEachMatchingRoutineOfItsOrgOnce()
    {
        var matching = await SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed);
        await SeedRoutineAsync(OrgA, RoutineEventType.BookingNew);
        await SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, active: false);
        await SeedRoutineAsync(OrgB, RoutineEventType.BookingConfirmed);
        var confirmed = Event(OrgA, DomainEventType.BookingConfirmed);

        await HandleAsync(confirmed);
        await HandleAsync(confirmed);

        var execution = Assert.Single(await ExecutionsAsync());
        Assert.Equal(matching.Id, execution.RoutineId);
        Assert.Equal(OrgA, execution.OrgId);
        Assert.Equal(confirmed.Id.ToString("D"), execution.EventId);
        Assert.Equal("Success", execution.Outcome);
    }

    [Theory]
    [InlineData(DomainEventType.ServiceRequestCreated)]
    [InlineData(DomainEventType.OctorateWebhookReceived)]
    [InlineData(DomainEventType.OctorateBookingStateChanged)]
    public async Task HandleAsync_UnmappableEvent_RunsNothing(DomainEventType type)
    {
        foreach (var routineType in Enum.GetValues<RoutineEventType>())
            await SeedRoutineAsync(OrgA, routineType);

        await HandleAsync(Event(OrgA, type));

        Assert.Empty(await ExecutionsAsync());
    }

    [Fact]
    public async Task HandleAsync_TimeTrigger_RunsOnlyTheRoutinesWithItsOffset()
    {
        var twoDays = await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckin, """{ "days": 2 }""");
        var fortyEightHours = await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckin, """{ "hours": 48 }""");
        await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckin, """{ "days": 1 }""");
        await SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckin, """{ "days": 2 }""");
        var bookingId = Guid.NewGuid();
        var trigger = Event(
            OrgA,
            DomainEventType.TimeTrigger,
            $$"""{"kind":"BeforeCheckIn","offset":48,"entityId":"{{bookingId}}","bookingId":"{{bookingId}}","propertyId":"{{Guid.NewGuid()}}"}""");

        await HandleAsync(trigger);

        Assert.Equal(
            new[] { twoDays.Id, fortyEightHours.Id }.Order(),
            (await ExecutionsAsync()).Select(e => e.RoutineId).Order());
    }

    [Fact]
    public async Task HandleAsync_FailingRoutine_IsRecordedOnItsExecution_AndTheEventDoesNotFail()
    {
        var broken = await SeedRoutineAsync(OrgA, RoutineEventType.PaymentFailed, actionsJson: "not a list of actions");
        var healthy = await SeedRoutineAsync(OrgA, RoutineEventType.PaymentFailed);

        await HandleAsync(Event(OrgA, DomainEventType.PaymentFailed));

        var executions = await ExecutionsAsync();
        Assert.Equal("Failed", executions.Single(e => e.RoutineId == broken.Id).Outcome);
        Assert.Equal("Success", executions.Single(e => e.RoutineId == healthy.Id).Outcome);
    }

    [Theory]
    [InlineData(DomainEventType.BookingCreated, RoutineEventType.BookingNew)]
    [InlineData(DomainEventType.BookingConfirmed, RoutineEventType.BookingConfirmed)]
    [InlineData(DomainEventType.BookingModified, RoutineEventType.BookingModified)]
    [InlineData(DomainEventType.BookingCancelled, RoutineEventType.BookingCanceled)]
    [InlineData(DomainEventType.CheckInStarted, RoutineEventType.CheckinOccurred)]
    [InlineData(DomainEventType.CheckInCompleted, RoutineEventType.OnlineCheckinCompleted)]
    [InlineData(DomainEventType.CheckOutCompleted, RoutineEventType.CheckoutOccurred)]
    [InlineData(DomainEventType.IdVerificationCompleted, RoutineEventType.IdVerificationCompleted)]
    [InlineData(DomainEventType.AlloggiatiReceiptArrived, RoutineEventType.AlloggiatiReceiptArrived)]
    [InlineData(DomainEventType.ServiceRequestCompleted, RoutineEventType.ActivityCompleted)]
    [InlineData(DomainEventType.PaymentSucceeded, RoutineEventType.PaymentSucceeded)]
    [InlineData(DomainEventType.PaymentFailed, RoutineEventType.PaymentFailed)]
    [InlineData(DomainEventType.GuestMessageReceived, RoutineEventType.GuestMessageReceived)]
    [InlineData(DomainEventType.ReviewReceived, RoutineEventType.ReviewReceived)]
    public void RoutineEventMap_DomainEvent_MapsToItsRoutineEvent(DomainEventType type, RoutineEventType expected)
    {
        Assert.Equal(expected, RoutineEventMap.Of(type));
    }

    [Fact]
    public void RoutineEventMap_TimeTriggerKinds_AreTheOnesTheEmitterWrites()
    {
        Assert.Null(RoutineEventMap.Of(DomainEventType.TimeTrigger));
        Assert.All(TimeTrigger.StayKinds, entry => Assert.Equal(entry.Key, RoutineEventMap.OfTimeTriggerKind(entry.Value)));
        Assert.Equal(RoutineEventType.WeeklyRecurrence, RoutineEventMap.OfTimeTriggerKind(TimeTrigger.Weekly));
        Assert.Null(RoutineEventMap.OfTimeTriggerKind("Sometimes"));
    }

    [Fact]
    public void RoutineEventDispatcher_IsGone_TheBusHandlerIsTheTriggerPath()
    {
        Assert.Null(typeof(RoutineService).Assembly.GetType("Casazen.Infrastructure.Services.RoutineEventDispatcher"));
        Assert.Contains(typeof(IEventHandler<DomainEvent>), typeof(RoutineEventHandler).GetInterfaces());
    }

    /// <summary>
    /// Proof of ADR-022-R7 (data rule) on PostgreSQL: the same event delivered twice executes a routine once, also when both
    /// deliveries run at once and both pass the "already executed?" read before either writes: the unique index on (routine,
    /// event) refuses the second execution and the engine takes it as "already executed", without an error.
    /// </summary>
    [PostgresFact]
    public async Task RoutineExecution_SameEvent_ExecutesOnce_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("routineonce");
        var routine = NewRoutine(OrgA, RoutineEventType.BookingConfirmed, "{}", "[]", active: true);
        await using (var seed = database.CreateContext())
        {
            seed.Routines.Add(routine);
            await seed.SaveChangesAsync();
        }

        // Two deliveries one after the other.
        var first = Event(OrgA, DomainEventType.BookingConfirmed);
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var db = database.CreateContext();
            await Handler(db).HandleAsync(first, CancellationToken.None);
        }

        // Two deliveries at the same time: both read "not executed yet", then both write.
        var second = Event(OrgA, DomainEventType.BookingConfirmed);
        var rendezvous = new ExecutionRendezvous(parties: 2);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .AddInterceptors(rendezvous)
            .Options;
        await using (var one = new AppDbContext(options))
        await using (var other = new AppDbContext(options))
        {
            await Task.WhenAll(
                Handler(one).HandleAsync(second, CancellationToken.None),
                Handler(other).HandleAsync(second, CancellationToken.None));
        }

        Assert.True(rendezvous.BothArrived, "The two deliveries did not reach the write together.");
        await using var read = database.CreateContext();
        var executions = await read.RoutineExecutions.AsNoTracking().Where(e => e.RoutineId == routine.Id).ToListAsync();
        Assert.Equal(2, executions.Count);
        Assert.Single(executions, e => e.EventId == first.Id.ToString("D"));
        Assert.Single(executions, e => e.EventId == second.Id.ToString("D"));
        Assert.All(executions, e => Assert.Equal("Success", e.Outcome));
    }

    private static RoutineEventHandler Handler(AppDbContext db) =>
        new(new RoutineService(db, NullLogger<RoutineService>.Instance), NullLogger<RoutineEventHandler>.Instance);

    private async Task HandleAsync(DomainEvent domainEvent)
    {
        await using var db = NewDb();
        await Handler(db).HandleAsync(domainEvent, CancellationToken.None);
    }

    private static DomainEvent Event(Guid orgId, DomainEventType type, string payloadJson = """{"BookingId":"6c1f2f0e-8f7e-4f42-9c1d-0d6b2f3e4a5b"}""") =>
        new(Guid.NewGuid(), orgId, type, Guid.NewGuid(), Now, payloadJson);

    private AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    private async Task<Routine> SeedRoutineAsync(
        Guid orgId,
        RoutineEventType type,
        string eventConfig = "{}",
        bool active = true,
        string actionsJson = "[]")
    {
        await using var db = NewDb();
        var routine = NewRoutine(orgId, type, eventConfig, actionsJson, active);
        db.Routines.Add(routine);
        await db.SaveChangesAsync();
        return routine;
    }

    private async Task<List<RoutineExecution>> ExecutionsAsync()
    {
        await using var db = NewDb();
        return await db.RoutineExecutions.AsNoTracking().ToListAsync();
    }

    private static Routine NewRoutine(Guid orgId, RoutineEventType type, string eventConfig, string actionsJson, bool active) => new()
    {
        OrgId = orgId,
        Name = $"{type} {eventConfig}",
        EventType = type,
        EventConfig = eventConfig,
        ActionsJson = actionsJson,
        Active = active,
    };

    /// <summary>
    /// Holds each context about to write a new execution until <paramref name="parties"/> of them are there, so the deliveries
    /// that run at once have all read "not executed yet" before any of them writes.
    /// </summary>
    private sealed class ExecutionRendezvous(int parties) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool BothArrived => _allArrived.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RoutineExecution>().Any(e => e.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref _arrived) == parties)
                    _allArrived.TrySetResult();
                await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return result;
        }
    }
}
