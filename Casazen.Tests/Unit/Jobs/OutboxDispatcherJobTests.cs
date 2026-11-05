using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Tests.Unit.BackgroundJobs;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Jobs;

/// <summary>
/// ADR-022-R4, R5, R18: <see cref="OutboxDispatcherJob"/> publishes the undispatched events in <c>OccurredAt</c> order, marks an event
/// dispatched only after every handler returned, retries a failing event at each run and dead-letters it after five attempts,
/// without holding back the other events.
/// </summary>
public class OutboxDispatcherJobTests
{
    private static readonly Guid OrgA = Guid.Parse("0f6c1a52-3b7e-4c11-9d2a-5e8b7c6d4a01");
    private static readonly Guid OrgB = Guid.Parse("8e2d4b19-6a5f-4f3c-b1e7-2c9a0d3e5f02");
    private static readonly DateTime Now = new(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc);

    private readonly string _database = $"outbox-dispatcher-{Guid.NewGuid()}";

    [Fact]
    public async Task ExecuteAsync_UndispatchedEvents_ArePublishedInOccurredAtOrderAndMarkedDispatched()
    {
        var third = await SeedAsync(OrgA, Now.AddMinutes(-1));
        var first = await SeedAsync(OrgB, Now.AddMinutes(-3));
        var second = await SeedAsync(OrgA, Now.AddMinutes(-2));
        await SeedAsync(OrgA, Now.AddMinutes(-4), dispatchedAt: Now.AddMinutes(-4));
        await SeedAsync(OrgA, Now.AddMinutes(-5), deadLettered: true);
        var bus = new RecordingBus();

        await RunAsync(bus);

        Assert.Equal([first.Id, second.Id, third.Id], bus.Published.Select(e => e.Id));
        Assert.Equal([OrgB, OrgA, OrgA], bus.Published.Select(e => e.OrgId));
        var rows = await RowsAsync();
        Assert.All(rows.Where(r => !r.DeadLettered), r => Assert.NotNull(r.DispatchedAt));
        Assert.Equal(Now, rows.Single(r => r.Id == first.Id).DispatchedAt);
        Assert.Null(rows.Single(r => r.DeadLettered).DispatchedAt);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchedAt_IsSetOnlyAfterEveryHandlerReturned()
    {
        var row = await SeedAsync(OrgA, Now.AddMinutes(-1));
        DateTime? dispatchedWhilePublishing = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bus = new RecordingBus(async e =>
        {
            await using var other = NewDb();
            dispatchedWhilePublishing = (await other.OutboxEvents.SingleAsync(r => r.Id == e.Id)).DispatchedAt;
        });

        await RunAsync(bus);

        Assert.Null(dispatchedWhilePublishing);
        Assert.Equal(Now, (await RowsAsync()).Single(r => r.Id == row.Id).DispatchedAt);
    }

    [Fact]
    public async Task ExecuteAsync_FailingEvent_IsTriedOncePerRunAndDeadLetteredAfterFiveAttempts_WithoutHoldingBackTheOthers()
    {
        var poison = await SeedAsync(OrgA, Now.AddMinutes(-10));
        var bus = new RecordingBus(e => e.Id == poison.Id
            ? throw new InvalidOperationException("handler down")
            : Task.CompletedTask);

        for (var run = 1; run <= OutboxDispatcherJob.MaxAttempts; run++)
        {
            var healthy = await SeedAsync(OrgA, Now.AddMinutes(-1));

            await RunAsync(bus);

            var rows = await RowsAsync();
            Assert.NotNull(rows.Single(r => r.Id == healthy.Id).DispatchedAt);
            var failed = rows.Single(r => r.Id == poison.Id);
            Assert.Equal(run, failed.Attempts);
            Assert.Equal("InvalidOperationException: handler down", failed.LastError);
            Assert.Null(failed.DispatchedAt);
            Assert.Equal(run == OutboxDispatcherJob.MaxAttempts, failed.DeadLettered);
        }

        await RunAsync(bus);

        Assert.Equal(OutboxDispatcherJob.MaxAttempts, bus.Published.Count(e => e.Id == poison.Id));
    }

    [Fact]
    public async Task ExecuteAsync_MoreEventsThanABatch_AreAllDispatchedByOneRun()
    {
        for (var i = 0; i < (2 * OutboxDispatcherJob.BatchSize) + 10; i++)
            await SeedAsync(OrgA, Now.AddSeconds(-i));
        var bus = new RecordingBus();

        await RunAsync(bus);

        Assert.Equal((2 * OutboxDispatcherJob.BatchSize) + 10, bus.Published.Select(e => e.Id).Distinct().Count());
        Assert.All(await RowsAsync(), r => Assert.NotNull(r.DispatchedAt));
    }

    /// <summary>
    /// The dispatcher reads every org as a system job, and the in-process bus gives each handler the org of its event, in the event
    /// and in the tenant of its own scope (ADR-022-R6).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithTheInProcessBus_EachHandlerRunsUnderTheOrgOfItsEvent()
    {
        var ofA = await SeedAsync(OrgA, Now.AddMinutes(-2));
        var ofB = await SeedAsync(OrgB, Now.AddMinutes(-1));
        var seen = new List<(Guid EventId, Guid EventOrg, Guid? TenantOrg)>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IBackgroundTenantScope>(sp => sp.GetRequiredService<TenantContext>());
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_database));
        services.AddSingleton(seen);
        services.AddScoped<IEventHandler<DomainEvent>, TenantProbeHandler>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var bus = new InProcessEventBus(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<InProcessEventBus>.Instance);

        await using (var jobScope = provider.CreateAsyncScope())
        {
            var job = new OutboxDispatcherJob(
                jobScope.ServiceProvider.GetRequiredService<AppDbContext>(),
                bus,
                new FixedTimeProvider(new DateTimeOffset(Now)),
                NullLogger<OutboxDispatcherJob>.Instance);
            await job.ExecuteAsync(CancellationToken.None);
        }

        Assert.Equal([(ofA.Id, OrgA, (Guid?)OrgA), (ofB.Id, OrgB, (Guid?)OrgB)], seen);
        Assert.All(await RowsAsync(), r => Assert.Equal(Now, r.DispatchedAt));
    }

    [Fact]
    public void Configure_RegistersTheDispatcherEveryMinute_NeverTwiceAtOnce()
    {
        var scheduled = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => scheduled[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        var registered = scheduled["outbox-dispatcher"];
        Assert.Equal("*/1 * * * *", registered.Cron);
        Assert.Equal(TimeZoneInfo.Utc, registered.Options.TimeZone);
        Assert.Equal(typeof(OutboxDispatcherJob), registered.Job.Type);
        var concurrency = typeof(OutboxDispatcherJob).GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.InRange(concurrency.TimeoutSec, 1, 59);
    }

    private async Task RunAsync(IEventBus bus)
    {
        await using var db = NewDb();
        await new OutboxDispatcherJob(db, bus, new FixedTimeProvider(new DateTimeOffset(Now)), NullLogger<OutboxDispatcherJob>.Instance)
            .ExecuteAsync(CancellationToken.None);
    }

    private AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    private async Task<OutboxEvent> SeedAsync(Guid orgId, DateTime occurredAt, DateTime? dispatchedAt = null, bool deadLettered = false)
    {
        await using var db = NewDb();
        var row = new OutboxEvent
        {
            OrgId = orgId,
            EventType = DomainEventType.BookingConfirmed,
            EntityId = Guid.NewGuid(),
            OccurredAt = occurredAt,
            PayloadJson = "{}",
            DispatchedAt = dispatchedAt,
            DeadLettered = deadLettered,
            Attempts = deadLettered ? OutboxDispatcherJob.MaxAttempts : 0,
        };
        db.OutboxEvents.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private async Task<List<OutboxEvent>> RowsAsync()
    {
        await using var db = NewDb();
        return await db.OutboxEvents.AsNoTracking().ToListAsync();
    }

    private sealed class RecordingBus(Func<DomainEvent, Task>? onPublish = null) : IEventBus
    {
        public List<DomainEvent> Published { get; } = [];

        public async Task PublishAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            Published.Add(domainEvent);
            if (onPublish is not null)
                await onPublish(domainEvent);
        }
    }

    public sealed class TenantProbeHandler(List<(Guid EventId, Guid EventOrg, Guid? TenantOrg)> seen, ITenantContext tenant)
        : IEventHandler<DomainEvent>
    {
        public Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            lock (seen)
                seen.Add((domainEvent.Id, domainEvent.OrgId, tenant.OrgId));
            return Task.CompletedTask;
        }
    }
}
