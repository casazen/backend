using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
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
/// ADR-022-R15, R21: <see cref="OutboxCleanupJob"/> deletes the dispatched events older than the retention and never a dead letter,
/// whose payload, error and entity are emptied once it is older than the retention. The deletion and the anonymization are set-based
/// SQL statements, so the proof runs on PostgreSQL.
/// </summary>
public class OutboxCleanupJobTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 3, 10, 0, DateTimeKind.Utc);
    private static readonly Guid OrgA = Guid.Parse("0f6c1a52-3b7e-4c11-9d2a-5e8b7c6d4a01");

    /// <summary>
    /// Proof of ADR-022-R15/R21 (data rule): a dead letter past the retention keeps its row with an empty payload, no error and no
    /// entity, and keeps its org, type, dates and attempts; dispatched events inside the retention and undispatched events are not
    /// deleted; dispatched events past the retention are.
    /// </summary>
    [PostgresFact]
    public async Task OutboxCleanup_DeadLetteredRows_AnonymizedNeverDeleted_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("outboxcleanup");
        var oldDispatched = Row(occurredDaysAgo: 70, dispatchedDaysAgo: 70);
        var youngDispatched = Row(occurredDaysAgo: 10, dispatchedDaysAgo: 10);
        var oldUndispatched = Row(occurredDaysAgo: 90, dispatchedDaysAgo: null);
        var oldDeadLetter = Row(occurredDaysAgo: 75, dispatchedDaysAgo: null, deadLettered: true);
        var youngDeadLetter = Row(occurredDaysAgo: 5, dispatchedDaysAgo: null, deadLettered: true);
        await using (var seed = database.CreateContext())
        {
            seed.OutboxEvents.AddRange(oldDispatched, youngDispatched, oldUndispatched, oldDeadLetter, youngDeadLetter);
            await seed.SaveChangesAsync();
        }

        await RunAsync(database, new EventBusOptions());
        await RunAsync(database, new EventBusOptions()); // a second night changes nothing more

        await using var read = database.CreateContext();
        var rows = await read.OutboxEvents.AsNoTracking().ToDictionaryAsync(e => e.Id);
        Assert.False(rows.ContainsKey(oldDispatched.Id));
        Assert.True(rows.ContainsKey(youngDispatched.Id));
        Assert.True(rows.ContainsKey(oldUndispatched.Id));

        var anonymized = rows[oldDeadLetter.Id];
        Assert.Equal("{}", anonymized.PayloadJson);
        Assert.Null(anonymized.LastError);
        Assert.Equal(Guid.Empty, anonymized.EntityId);
        Assert.Equal(OrgA, anonymized.OrgId);
        Assert.Equal(DomainEventType.BookingConfirmed, anonymized.EventType);
        Assert.Equal(oldDeadLetter.OccurredAt, anonymized.OccurredAt);
        Assert.Equal(OutboxDispatcherJob.MaxAttempts, anonymized.Attempts);
        Assert.True(anonymized.DeadLettered);

        var kept = rows[youngDeadLetter.Id];
        Assert.Equal(youngDeadLetter.PayloadJson, kept.PayloadJson);
        Assert.Equal(youngDeadLetter.LastError, kept.LastError);
        Assert.Equal(youngDeadLetter.EntityId, kept.EntityId);
    }

    [PostgresFact]
    public async Task ExecuteAsync_RetentionBelowThirtyDays_KeepsThirtyDays()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("outboxcleanup");
        var twentyDays = Row(occurredDaysAgo: 20, dispatchedDaysAgo: 20);
        var fortyDays = Row(occurredDaysAgo: 40, dispatchedDaysAgo: 40);
        await using (var seed = database.CreateContext())
        {
            seed.OutboxEvents.AddRange(twentyDays, fortyDays);
            await seed.SaveChangesAsync();
        }

        await RunAsync(database, new EventBusOptions { RetentionDays = 7 });

        await using var read = database.CreateContext();
        Assert.Equal([twentyDays.Id], await read.OutboxEvents.Select(e => e.Id).ToListAsync());
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(90, 90)]
    [InlineData(30, 30)]
    [InlineData(29, 30)]
    [InlineData(0, 30)]
    public void EffectiveRetentionDays_NeverBelowThirty(int configured, int effective)
    {
        Assert.Equal(effective, new EventBusOptions { RetentionDays = configured }.EffectiveRetentionDays);
        Assert.Equal(60, new EventBusOptions().RetentionDays);
    }

    [Fact]
    public void Configure_RegistersTheCleanupDaily_NeverTwiceAtOnce()
    {
        var scheduled = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => scheduled[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        var registered = scheduled["outbox-cleanup"];
        Assert.Equal("10 3 * * *", registered.Cron);
        Assert.Equal(TimeZoneInfo.Utc, registered.Options.TimeZone);
        Assert.Equal(typeof(OutboxCleanupJob), registered.Job.Type);
        Assert.NotNull(typeof(OutboxCleanupJob).GetCustomAttribute<DisableConcurrentExecutionAttribute>());
    }

    private static async Task RunAsync(PostgresTestDatabase database, EventBusOptions options)
    {
        await using var db = database.CreateContext();
        await new OutboxCleanupJob(
                db,
                Options.Create(options),
                new FixedTimeProvider(new DateTimeOffset(Now)),
                NullLogger<OutboxCleanupJob>.Instance)
            .ExecuteAsync(CancellationToken.None);
    }

    private static OutboxEvent Row(int occurredDaysAgo, int? dispatchedDaysAgo, bool deadLettered = false) => new()
    {
        OrgId = OrgA,
        EventType = DomainEventType.BookingConfirmed,
        EntityId = Guid.NewGuid(),
        OccurredAt = Now.AddDays(-occurredDaysAgo),
        PayloadJson = $$"""{"bookingId":"{{Guid.NewGuid()}}"}""",
        DispatchedAt = dispatchedDaysAgo is { } days ? Now.AddDays(-days) : null,
        DeadLettered = deadLettered,
        Attempts = deadLettered ? OutboxDispatcherJob.MaxAttempts : 0,
        LastError = deadLettered ? "InvalidOperationException: handler down" : null,
    };
}
