using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.BackgroundJobs;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Jobs;

/// <summary>
/// ADR-022-R8: <see cref="TimeEventEmitterJob"/> writes a time trigger for each stay and offset an active routine of its org asks
/// for, when it becomes due (the stay date at Rome midnight shifted by the offset), once, whatever the number of runs.
/// </summary>
public class TimeEventEmitterJobTests
{
    private static readonly Guid OrgA = Guid.Parse("0f6c1a52-3b7e-4c11-9d2a-5e8b7c6d4a01");
    private static readonly Guid OrgB = Guid.Parse("8e2d4b19-6a5f-4f3c-b1e7-2c9a0d3e5f02");

    // Check-in on 10 November, check-out on 12 November (winter time, Rome is UTC+1): Rome midnight of the 10th is 23:00 UTC
    // of the 9th, so "2 days before check-in" is due at 23:00 UTC on 7 November, "3 hours after check-out" at 02:00 UTC on 12 November.
    private static readonly DateTime CheckIn = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CheckOut = new(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TwoDaysBeforeCheckIn = new(2026, 11, 7, 23, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ThreeHoursAfterCheckOut = new(2026, 11, 12, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _database = $"time-emitter-{Guid.NewGuid()}";

    [Fact]
    public async Task ExecuteAsync_StaysOfTheOrgsWithATimeRoutine_EmitOneTriggerEachWhenDue()
    {
        await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckin, """{ "days": 2 }""");
        await SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckout, """{ "hours": 3 }""");
        await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckout, """{ "days": 1 }""", active: false);
        await SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, """{ "days": 1 }""");
        var stay = await SeedBookingAsync(OrgA, BookingStatus.Confirmed);
        await SeedBookingAsync(OrgA, BookingStatus.Pending);
        await SeedBookingAsync(OrgA, BookingStatus.Cancelled);
        await SeedBookingAsync(OrgB, BookingStatus.Confirmed); // no routine in org B

        await RunAsync(TwoDaysBeforeCheckIn.AddMinutes(-5)); // not due yet
        Assert.Empty(await EventsAsync());

        await RunAsync(TwoDaysBeforeCheckIn.AddMinutes(2));
        await RunAsync(TwoDaysBeforeCheckIn.AddMinutes(7)); // the next run, overlapping window: nothing new
        await RunAsync(ThreeHoursAfterCheckOut.AddMinutes(1));

        var events = await EventsAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(OrgA, e.OrgId);
            Assert.Equal(DomainEventType.TimeTrigger, e.EventType);
            Assert.Equal(stay.Id, e.EntityId);
        });
        Assert.Equal(
            [$"BeforeCheckIn:48:{stay.Id}:2026-11-07T23Z", $"AfterCheckOut:3:{stay.Id}:2026-11-12T02Z"],
            events.OrderBy(e => e.OccurredAt).Select(e => e.DedupKey));
        Assert.Equal(
            $$"""{"kind":"BeforeCheckIn","offset":48,"entityId":"{{stay.Id}}","bookingId":"{{stay.Id}}","propertyId":"{{stay.PropertyId}}"}""",
            events.OrderBy(e => e.OccurredAt).First().PayloadJson);
    }

    [Fact]
    public async Task ExecuteAsync_TheSameOffsetWrittenInDaysOrInHours_IsOneTrigger()
    {
        await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckin, """{ "days": 2 }""");
        await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckin, """{ "hours": 48 }""");
        await SeedBookingAsync(OrgA, BookingStatus.CheckedIn);

        await RunAsync(TwoDaysBeforeCheckIn.AddMinutes(2));

        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_TriggerDueBeforeTheCatchUpWindow_IsNotEmittedLate()
    {
        await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckin, """{ "days": 2 }""");
        await SeedBookingAsync(OrgA, BookingStatus.Confirmed);

        await RunAsync(TwoDaysBeforeCheckIn + TimeEventEmitterJob.CatchUpWindow + TimeSpan.FromMinutes(1));

        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_SummerTime_IsDueAtRomeMidnight()
    {
        // 10 July: Rome is UTC+2, its midnight is 22:00 UTC of the 9th; one day before check-in is 22:00 UTC on the 8th.
        await SeedRoutineAsync(OrgA, RoutineEventType.BeforeCheckin, """{ "days": 1 }""");
        var stay = await SeedBookingAsync(OrgA, BookingStatus.Confirmed, new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc));

        await RunAsync(new DateTime(2026, 7, 8, 21, 59, 0, DateTimeKind.Utc));
        Assert.Empty(await EventsAsync());
        await RunAsync(new DateTime(2026, 7, 8, 22, 0, 0, DateTimeKind.Utc));

        Assert.Equal($"BeforeCheckIn:24:{stay.Id}:2026-07-08T22Z", Assert.Single(await EventsAsync()).DedupKey);
    }

    [Theory]
    [InlineData("""{ "days": 2 }""", 48)]
    [InlineData("""{ "hours": 48 }""", 48)]
    [InlineData("""{ "Days": 1, "Hours": 3 }""", 27)]
    [InlineData("{}", 0)]
    [InlineData("", 0)]
    [InlineData("""{ "days": -1 }""", null)]
    [InlineData("""{ "days": 1.5 }""", null)]
    [InlineData("""{ "days": "2" }""", null)]
    [InlineData("""{ "days": 400 }""", null)]
    [InlineData("[2]", null)]
    [InlineData("not json", null)]
    public void OffsetHours_RoutineConfiguration_IsWholeHours(string eventConfig, int? expected)
    {
        Assert.Equal(expected, TimeTrigger.OffsetHours(eventConfig));
    }

    [Theory]
    [InlineData(5, "*/5 * * * *")]
    [InlineData(15, "*/15 * * * *")]
    [InlineData(0, "*/1 * * * *")]
    [InlineData(120, "*/59 * * * *")]
    public void Configure_RegistersTheEmitterEveryEvaluationInterval(int minutes, string cron)
    {
        var scheduled = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, c, o) => scheduled[id] = (job, c, o));

        RecurringJobsRegistration.Configure(
            manager.Object,
            RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false),
            new RoutineOptions { EvaluationIntervalMinutes = minutes });

        var registered = scheduled[TimeEventEmitterJob.RecurringJobId];
        Assert.Equal("time-event-emitter", TimeEventEmitterJob.RecurringJobId);
        Assert.Equal(cron, registered.Cron);
        Assert.Equal(TimeZoneInfo.Utc, registered.Options.TimeZone);
        Assert.Equal(typeof(TimeEventEmitterJob), registered.Job.Type);
        Assert.NotNull(typeof(TimeEventEmitterJob).GetCustomAttribute<DisableConcurrentExecutionAttribute>());
    }

    /// <summary>
    /// On PostgreSQL the unique index of the org is the last guard: two runs of the same window at once write each trigger once,
    /// and the same key is refused in its org only, while events without a key are never compared.
    /// </summary>
    [PostgresFact]
    public async Task ExecuteAsync_TwoRunsAtOnce_WriteEachTriggerOnce()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("timeemitter");
        Booking stay;
        await using (var seed = database.CreateContext())
        {
            var org = new OrgEntity { Id = OrgA, Name = "Org emitter", Slug = $"org-emitter-{Guid.NewGuid():N}", DisplayName = "Org emitter" };
            var property = new Property { OwnerId = "auth0|emitter-owner", OrgId = OrgA, Name = "Casa", Address = "Via Roma 1", City = "Roma" };
            var guest = new Guest
            {
                OrgId = OrgA,
                FirstName = "Mario",
                LastName = "Rossi",
                Email = $"mario.{Guid.NewGuid():N}@example.com",
                DataProcessingPurpose = "Booking Management",
            };
            stay = NewBooking(OrgA, BookingStatus.Confirmed, CheckIn);
            stay.PropertyId = property.Id;
            stay.GuestId = guest.Id;
            stay.NumberOfGuests = 2;
            seed.Orgs.Add(org);
            seed.Properties.Add(property);
            seed.Guests.Add(guest);
            seed.Bookings.Add(stay);
            seed.Routines.Add(NewRoutine(OrgA, RoutineEventType.BeforeCheckin, """{ "days": 2 }""", active: true));
            await seed.SaveChangesAsync();
        }

        var now = TwoDaysBeforeCheckIn.AddMinutes(2);
        await using (var first = database.CreateContext())
        await using (var second = database.CreateContext())
        {
            await Task.WhenAll(Job(first, now).ExecuteAsync(CancellationToken.None), Job(second, now).ExecuteAsync(CancellationToken.None));
        }

        await using var read = database.CreateContext();
        var trigger = Assert.Single(await read.OutboxEvents.ToListAsync());
        Assert.Equal($"BeforeCheckIn:48:{stay.Id}:2026-11-07T23Z", trigger.DedupKey);

        // The same key in the same org is refused; in another org, and without a key, it is not.
        read.OutboxEvents.Add(new OutboxEvent { OrgId = OrgB, EventType = DomainEventType.TimeTrigger, DedupKey = trigger.DedupKey, OccurredAt = now });
        read.OutboxEvents.Add(new OutboxEvent { OrgId = OrgA, EventType = DomainEventType.BookingCreated, OccurredAt = now });
        read.OutboxEvents.Add(new OutboxEvent { OrgId = OrgA, EventType = DomainEventType.BookingCreated, OccurredAt = now });
        await read.SaveChangesAsync();
        read.OutboxEvents.Add(new OutboxEvent { OrgId = OrgA, EventType = DomainEventType.TimeTrigger, DedupKey = trigger.DedupKey, OccurredAt = now });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => read.SaveChangesAsync());
        Assert.Equal(OutboxEvent.DedupIndexName, Assert.IsType<Npgsql.PostgresException>(error.InnerException).ConstraintName);
    }

    private async Task RunAsync(DateTime utcNow)
    {
        await using var db = NewDb();
        await Job(db, utcNow).ExecuteAsync(CancellationToken.None);
    }

    private static TimeEventEmitterJob Job(AppDbContext db, DateTime utcNow)
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(utcNow, TimeSpan.Zero));
        return new TimeEventEmitterJob(
            db,
            new DomainEventRecorder(db, NullTenantContext.Instance, clock),
            Options.Create(new RoutineOptions()),
            clock,
            NullLogger<TimeEventEmitterJob>.Instance);
    }

    private AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    private async Task<List<OutboxEvent>> EventsAsync()
    {
        await using var db = NewDb();
        return await db.OutboxEvents.ToListAsync();
    }

    private async Task SeedRoutineAsync(Guid orgId, RoutineEventType type, string eventConfig, bool active = true)
    {
        await using var db = NewDb();
        db.Routines.Add(NewRoutine(orgId, type, eventConfig, active));
        await db.SaveChangesAsync();
    }

    private async Task<Booking> SeedBookingAsync(Guid orgId, BookingStatus status, DateTime? checkIn = null)
    {
        await using var db = NewDb();
        var booking = NewBooking(orgId, status, checkIn ?? CheckIn);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static Routine NewRoutine(Guid orgId, RoutineEventType type, string eventConfig, bool active) => new()
    {
        OrgId = orgId,
        Name = $"{type} {eventConfig}",
        EventType = type,
        EventConfig = eventConfig,
        Active = active,
    };

    private static Booking NewBooking(Guid orgId, BookingStatus status, DateTime checkIn) => new()
    {
        OrgId = orgId,
        PropertyId = Guid.NewGuid(),
        GuestId = Guid.NewGuid(),
        CheckInDate = checkIn,
        CheckOutDate = checkIn + (CheckOut - CheckIn),
        Status = status,
    };
}
