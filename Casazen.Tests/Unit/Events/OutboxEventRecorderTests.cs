using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Events;

/// <summary>
/// ADR-022-R1: a domain change records its event through <see cref="DomainEventRecorder"/>, which only adds an
/// <see cref="OutboxEvent"/> to the current unit of work; the caller's single <c>SaveChangesAsync</c> commits the change and the
/// event together. The transaction itself is proved on PostgreSQL (<see cref="OutboxEventRecorder_TransactionSafety_Test"/>).
/// </summary>
public class OutboxEventRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid OrgA = Guid.Parse("0f6c1a52-3b7e-4c11-9d2a-5e8b7c6d4a01");
    private static readonly Guid OrgB = Guid.Parse("8e2d4b19-6a5f-4f3c-b1e7-2c9a0d3e5f02");

    [Fact]
    public async Task Record_AddsTheEventToTheUnitOfWork_WithoutSavingIt()
    {
        var database = Guid.NewGuid().ToString();
        await using var db = NewInMemory(database);
        var bookingId = Guid.NewGuid();

        Recorder(db).Record(DomainEventType.BookingConfirmed, bookingId, new { BookingId = bookingId, Nights = 3 }, orgId: OrgA);

        var added = Assert.Single(db.ChangeTracker.Entries<OutboxEvent>());
        Assert.Equal(EntityState.Added, added.State);
        await using (var other = NewInMemory(database))
            Assert.Empty(await other.OutboxEvents.ToListAsync());

        await db.SaveChangesAsync();

        await using var read = NewInMemory(database);
        var row = Assert.Single(await read.OutboxEvents.ToListAsync());
        Assert.Equal(OrgA, row.OrgId);
        Assert.Equal(DomainEventType.BookingConfirmed, row.EventType);
        Assert.Equal(bookingId, row.EntityId);
        Assert.Equal(Now.UtcDateTime, row.OccurredAt);
        Assert.Equal($$"""{"BookingId":"{{bookingId}}","Nights":3}""", row.PayloadJson);
        Assert.Null(row.DedupKey);
        Assert.Null(row.DispatchedAt);
        Assert.Equal(0, row.Attempts);
        Assert.False(row.DeadLettered);
    }

    [Fact]
    public void Record_InsideARequest_UsesTheOrgOfTheRequest_UnlessTheCallerNamesOne()
    {
        using var db = NewInMemory(Guid.NewGuid().ToString(), new RequestTenant(OrgA));
        var recorder = new DomainEventRecorder(db, new RequestTenant(OrgA), new FixedTimeProvider(Now));

        recorder.Record(DomainEventType.BookingCreated, Guid.NewGuid(), new { });
        recorder.Record(DomainEventType.ServiceRequestCompleted, Guid.NewGuid(), new { }, orgId: OrgB);

        var orgs = db.ChangeTracker.Entries<OutboxEvent>().Select(e => (e.Entity.EventType, e.Entity.OrgId)).ToList();
        Assert.Contains((DomainEventType.BookingCreated, OrgA), orgs);
        Assert.Contains((DomainEventType.ServiceRequestCompleted, OrgB), orgs);
    }

    [Fact]
    public void Record_OutsideARequestWithoutAnOrg_IsRefusedAndAddsNothing()
    {
        using var db = NewInMemory(Guid.NewGuid().ToString());

        Assert.Throws<InvalidOperationException>(() => Recorder(db).Record(DomainEventType.TimeTrigger, Guid.NewGuid(), new { }));
        Assert.Throws<ArgumentException>(() => Recorder(db).Record(DomainEventType.TimeTrigger, Guid.NewGuid(), new { }, orgId: Guid.Empty));
        Assert.Throws<ArgumentException>(
            () => Recorder(db).Record(DomainEventType.TimeTrigger, Guid.NewGuid(), new { }, dedupKey: new string('k', 201), orgId: OrgA));

        Assert.Empty(db.ChangeTracker.Entries<OutboxEvent>());
    }

    /// <summary>
    /// Proof of ADR-022-R1 on PostgreSQL: the domain change and its event commit together. A change rolled back takes its event with
    /// it, a change the database refuses leaves no event behind, and a committed change has its event.
    /// </summary>
    [PostgresFact]
    public async Task OutboxEventRecorder_TransactionSafety_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("outbox");

        // 1. The use case's transaction is rolled back after SaveChanges: neither the change nor its event stays.
        var rolledBack = NewRoutine("rolled back");
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Routines.Add(rolledBack);
            Recorder(db).Record(DomainEventType.BookingConfirmed, rolledBack.Id, new { RoutineId = rolledBack.Id }, orgId: OrgA);
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        // 2. The database refuses the change (a foreign key): the event saved by the same SaveChanges is refused with it.
        var refusedEntity = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.RoutineExecutions.Add(new RoutineExecution { OrgId = OrgA, RoutineId = Guid.NewGuid(), EventId = "no-such-routine", Outcome = "Running" });
            Recorder(db).Record(DomainEventType.BookingModified, refusedEntity, new { }, orgId: OrgA);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // 3. Committed: the change and its event are both there.
        var committed = NewRoutine("committed");
        await using (var db = database.CreateContext())
        {
            db.Routines.Add(committed);
            Recorder(db).Record(DomainEventType.BookingCreated, committed.Id, new { RoutineId = committed.Id }, orgId: OrgA);
            await db.SaveChangesAsync();
        }

        await using var read = database.CreateContext();
        Assert.False(await read.Routines.AnyAsync(r => r.Id == rolledBack.Id));
        Assert.False(await read.OutboxEvents.AnyAsync(e => e.EntityId == rolledBack.Id));
        Assert.False(await read.OutboxEvents.AnyAsync(e => e.EntityId == refusedEntity));
        Assert.True(await read.Routines.AnyAsync(r => r.Id == committed.Id));
        var outbox = Assert.Single(await read.OutboxEvents.ToListAsync());
        Assert.Equal(committed.Id, outbox.EntityId);
        Assert.Equal(OrgA, outbox.OrgId);
        Assert.Equal(DomainEventType.BookingCreated, outbox.EventType);
    }

    private static DomainEventRecorder Recorder(AppDbContext db) =>
        new(db, NullTenantContext.Instance, new FixedTimeProvider(Now));

    private static AppDbContext NewInMemory(string database, ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options, tenant);

    private static Routine NewRoutine(string name) => new()
    {
        OrgId = OrgA,
        Name = name,
        EventType = RoutineEventType.BookingConfirmed,
        Active = false,
    };

    /// <summary>The tenant of an authenticated request of <paramref name="orgId"/>.</summary>
    private sealed class RequestTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
