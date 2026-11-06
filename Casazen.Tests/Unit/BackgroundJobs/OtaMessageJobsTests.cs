using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Octorate;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// The two jobs of the OTA conversations (E05/F01/US01/T03, ADR-032, NF-62, OCT-09): the pass every 10 minutes and the deep pass every night. Every
/// org whose connection is connected is read for itself, a rate limit ends the run of that org only, a failure of one org never stops the others, the
/// confirmed listings with no history are imported within a budget, and the jobs are scheduled only while <c>Features:OctorateConnector</c> is on,
/// each with the lock that keeps two runs from overlapping (FD-11). The sync service is a stub the test sets up: what is proven is what the jobs do
/// with its answers.
/// </summary>
public class OtaMessageJobsTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-aaaa-4aaa-8aaa-000000000001");
    private static readonly Guid OrgB = Guid.Parse("22222222-bbbb-4bbb-8bbb-000000000002");
    private static readonly Guid OrgC = Guid.Parse("33333333-cccc-4ccc-8ccc-000000000003");
    private static readonly Guid PropertyA1 = Guid.Parse("aaaaaaaa-0001-4000-8000-000000000001");
    private static readonly Guid PropertyA2 = Guid.Parse("aaaaaaaa-0002-4000-8000-000000000002");
    private static readonly Guid PropertyB1 = Guid.Parse("bbbbbbbb-0001-4000-8000-000000000003");

    [Fact]
    public async Task ReconcileJob_AnOrgThatUsedItsCalls_IsStoppedAndTheOthersAreReadAndImported()
    {
        var kit = new Kit();
        kit.Sync.Setup(s => s.ReconcileRecentAsync(OrgA, It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateRateLimitException(429));
        kit.Sync.Setup(s => s.ReconcileRecentAsync(OrgB, It.IsAny<CancellationToken>())).ReturnsAsync(new SyncResult(2, 1, 0, 0));

        await using (var db = kit.NewDb())
            await kit.ReconcileJob(db).ExecuteAsync(CancellationToken.None);

        // The disconnected org is not read; the one that used its calls is stopped: no import is started for it (the checkpoints continue it next time).
        kit.Sync.Verify(s => s.ReconcileRecentAsync(OrgC, It.IsAny<CancellationToken>()), Times.Never);
        kit.Sync.Verify(s => s.ReconcileRecentAsync(OrgA, It.IsAny<CancellationToken>()), Times.Once);
        kit.Sync.Verify(s => s.ReconcileRecentAsync(OrgB, It.IsAny<CancellationToken>()), Times.Once);
        kit.Sync.Verify(s => s.ImportPropertyAsync(PropertyA1, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);
        kit.Sync.Verify(s => s.ImportPropertyAsync(PropertyA2, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);

        // The other org's listing with no history is imported, within a slice of the budget.
        kit.Sync.Verify(
            s => s.ImportPropertyAsync(PropertyB1, It.IsAny<CancellationToken>(), It.Is<TimeSpan?>(b => b != null && b <= OtaMessageReconcileJob.ImportSlice)),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileJob_ImportsOnlyTheConfirmedListingsWhoseHistoryIsNotThere_AndAFailureDoesNotStopTheNext()
    {
        var kit = new Kit();
        kit.Sync.Setup(s => s.ImportPropertyAsync(PropertyA1, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await using (var db = kit.NewDb())
            await kit.ReconcileJob(db).ExecuteAsync(CancellationToken.None);

        // The listing whose import finished (A2) is not imported again; the one that fails (A1) does not keep the one of the other org (B1) waiting.
        kit.Sync.Verify(s => s.ImportPropertyAsync(PropertyA1, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Once);
        kit.Sync.Verify(s => s.ImportPropertyAsync(PropertyA2, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);
        kit.Sync.Verify(s => s.ImportPropertyAsync(PropertyB1, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Once);
        kit.Sync.Verify(s => s.ReconcileRecentAsync(OrgB, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileJob_AListingWhoseChatIsNotInTheLicense_IsLeftAloneForAnHourAndThenAskedAgain()
    {
        var kit = new Kit();
        kit.AddState(OrgA, "111", PropertyA1, OtaMessageSyncErrorCodes.ChatNotLicensed, minutesAgo: 5);

        await using (var db = kit.NewDb())
            await kit.ReconcileJob(db).ExecuteAsync(CancellationToken.None);
        kit.Sync.Verify(s => s.ImportPropertyAsync(PropertyA1, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);

        // An hour and more later the module may be in the license: the listing is tried again.
        var later = new Kit();
        later.AddState(OrgA, "111", PropertyA1, OtaMessageSyncErrorCodes.ChatNotLicensed, minutesAgo: 120);
        await using (var db = later.NewDb())
            await later.ReconcileJob(db).ExecuteAsync(CancellationToken.None);
        later.Sync.Verify(s => s.ImportPropertyAsync(PropertyA1, It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Once);
    }

    [Fact]
    public async Task DeepJob_ReadsEveryConnectedOrgWithTheOverlapOfTwoDays_AndAFailureDoesNotStopTheOthers()
    {
        var kit = new Kit();
        kit.Sync.Setup(s => s.ReconcileDeepAsync(OrgA, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(500));

        await using (var db = kit.NewDb())
            await kit.DeepJob(db).ExecuteAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromDays(2), OtaMessageDeepReconcileJob.Overlap);
        kit.Sync.Verify(s => s.ReconcileDeepAsync(OrgA, TimeSpan.FromDays(2), It.IsAny<CancellationToken>()), Times.Once);
        kit.Sync.Verify(s => s.ReconcileDeepAsync(OrgB, TimeSpan.FromDays(2), It.IsAny<CancellationToken>()), Times.Once);
        kit.Sync.Verify(s => s.ReconcileDeepAsync(OrgC, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Jobs_WithTheConnectorOff_DoNothing()
    {
        var kit = new Kit();
        kit.Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(false);

        await using (var db = kit.NewDb())
        {
            await kit.ReconcileJob(db).ExecuteAsync(CancellationToken.None);
            await kit.DeepJob(db).ExecuteAsync(CancellationToken.None);
        }

        kit.Sync.VerifyNoOtherCalls();
    }

    [Fact]
    public void Configure_OctorateConnectorOn_SchedulesThePassEveryTenMinutesAndTheDeepPassAt0230Utc()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(true));

        Assert.Equal("ota-message-reconcile", OtaMessageReconcileJob.RecurringJobId);
        var recent = jobs[OtaMessageReconcileJob.RecurringJobId];
        Assert.Equal("*/10 * * * *", recent.Cron);
        Assert.Equal(TimeZoneInfo.Utc, recent.Options.TimeZone);
        Assert.Equal(nameof(OtaMessageReconcileJob.ExecuteAsync), recent.Job.Method.Name);

        Assert.Equal("ota-message-deep-reconcile", OtaMessageDeepReconcileJob.RecurringJobId);
        var deep = jobs[OtaMessageDeepReconcileJob.RecurringJobId];
        Assert.Equal("30 2 * * *", deep.Cron);
        Assert.Equal(TimeZoneInfo.Utc, deep.Options.TimeZone);
        Assert.Equal(nameof(OtaMessageDeepReconcileJob.ExecuteAsync), deep.Job.Method.Name);
        manager.Verify(m => m.RemoveIfExists(It.Is<string>(id => id.StartsWith("ota-message", StringComparison.Ordinal))), Times.Never);
    }

    [Fact]
    public void Configure_OctorateConnectorOff_RemovesTheScheduleOfTheMessageJobs()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(false));

        Assert.DoesNotContain(OtaMessageReconcileJob.RecurringJobId, jobs.Keys);
        Assert.DoesNotContain(OtaMessageDeepReconcileJob.RecurringJobId, jobs.Keys);
        manager.Verify(m => m.RemoveIfExists(OtaMessageReconcileJob.RecurringJobId), Times.Once);
        manager.Verify(m => m.RemoveIfExists(OtaMessageDeepReconcileJob.RecurringJobId), Times.Once);
    }

    [Fact]
    public void TheMessageJobs_HoldALockSoTwoRunsNeverOverlap_AndWaitForItAsLongAsTheirKindOfJob()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(true));

        foreach (var id in new[] { OtaMessageReconcileJob.RecurringJobId, OtaMessageDeepReconcileJob.RecurringJobId })
        {
            var method = jobs[id].Job.Method;
            var attribute = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>()
                ?? method.DeclaringType?.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
            Assert.True(attribute is not null, $"Recurring job '{id}' has no [DisableConcurrentExecution]");
            Assert.InRange(attribute.TimeoutSec, 1, (int)HangfireStorageSettings.DefaultDistributedLockTimeout.TotalSeconds);
        }

        // The jobs live in the infrastructure project, which cannot see JobLockTimeouts (web): the numbers are kept equal here.
        Assert.Equal(JobLockTimeouts.FrequentSeconds, OtaMessageReconcileJob.LockTimeoutSeconds);
        Assert.Equal(JobLockTimeouts.DefaultSeconds, OtaMessageDeepReconcileJob.LockTimeoutSeconds);
    }

    [Fact]
    public void AddMessageSync_RegistersTheServiceTheHandlerTheJobsAndTheRetention()
    {
        var services = new ServiceCollection();
        services.AddMessageSync();

        Assert.Contains(services, d => d.ServiceType == typeof(IMessageSyncService) && d.ImplementationType == typeof(MessageSyncService));

        // One pacing for the scope: the handler sets it not to wait and the service of the same scope reads it.
        Assert.Contains(services, d => d.ServiceType == typeof(MessageSyncPacing) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IEventHandler<OctorateWebhookReceivedEvent>) && d.ImplementationType == typeof(ChatMessageReceivedHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(OtaMessageReconcileJob) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(OtaMessageDeepReconcileJob) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IOtaMessageRetentionService) && d.ImplementationType == typeof(Casazen.Infrastructure.Services.OtaMessageRetentionService));
    }

    private static IFeatureFlags Flags(bool octorateConnector)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(octorateConnector);
        return flags.Object;
    }

    private static (Mock<IRecurringJobManager> Manager, Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Jobs) Manager()
    {
        var jobs = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => jobs[id] = (job, cron, options));
        return (manager, jobs);
    }

    /// <summary>
    /// Three orgs: A and B connected, C disconnected. A has two confirmed listings, one with its history imported; B has one with none. The sync service
    /// answers an empty result to anything the test does not set up.
    /// </summary>
    private sealed class Kit
    {
        private readonly string _database = $"ota-message-jobs-{Guid.NewGuid():N}";
        private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));

        public Kit()
        {
            using var seed = NewDb();
            var connectionA = Connection(OrgA, OctorateConnectionStatus.Connected);
            seed.OctorateConnections.AddRange(connectionA, Connection(OrgB, OctorateConnectionStatus.Connected), Connection(OrgC, OctorateConnectionStatus.Disconnected));
            seed.OtaMessageSyncStates.Add(new OtaMessageSyncState
            {
                OrgId = OrgA,
                OctorateAccommodationId = "222",
                ConnectionId = connectionA.Id,
                PropertyId = PropertyA2,
                InitialImportCompletedAt = _clock.GetUtcNow().UtcDateTime.AddDays(-1),
                UpdatedAt = _clock.GetUtcNow().UtcDateTime.AddDays(-1),
            });
            seed.SaveChanges();

            Sync.Setup(s => s.ReconcileRecentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(SyncResult.Empty);
            Sync.Setup(s => s.ReconcileDeepAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(SyncResult.Empty);
            Sync.Setup(s => s.ImportPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>())).ReturnsAsync(SyncResult.Empty);
            Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
            Host.SetupGet(h => h.EnvironmentName).Returns("Staging");
            Mappings
                .Setup(m => m.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid org, CancellationToken _) => RowsOf(org));
        }

        public Mock<IMessageSyncService> Sync { get; } = new();

        public Mock<IChannelMappingService> Mappings { get; } = new();

        public Mock<IFeatureFlags> Flags { get; } = new();

        public Mock<IHostEnvironment> Host { get; } = new();

        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

        public OtaMessageReconcileJob ReconcileJob(AppDbContext db) =>
            new(db, Sync.Object, Mappings.Object, new OctorateOrgScope(NullTenantContext.Instance), Flags.Object, Host.Object, _clock, NullLogger<OtaMessageReconcileJob>.Instance);

        public OtaMessageDeepReconcileJob DeepJob(AppDbContext db) =>
            new(db, Sync.Object, new OctorateOrgScope(NullTenantContext.Instance), Flags.Object, Host.Object, NullLogger<OtaMessageDeepReconcileJob>.Instance);

        /// <summary>The checkpoints of a listing of an org of the kit, worked on <paramref name="minutesAgo"/> minutes ago, with the error it kept.</summary>
        public void AddState(Guid orgId, string accommodation, Guid propertyId, string? lastError, int minutesAgo)
        {
            using var db = NewDb();
            var connectionId = db.OctorateConnections.Single(c => c.OrgId == orgId).Id;
            db.OtaMessageSyncStates.Add(new OtaMessageSyncState
            {
                OrgId = orgId,
                OctorateAccommodationId = accommodation,
                ConnectionId = connectionId,
                PropertyId = propertyId,
                LastError = lastError,
                UpdatedAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(-minutesAgo),
            });
            db.SaveChanges();
        }

        private static IReadOnlyList<ChannelMappingRow> RowsOf(Guid org)
        {
            if (org == OrgA)
            {
                return
                [
                    MessageSyncHarness.Row("111", OctorateMappingStatus.Confirmed, PropertyA1),
                    MessageSyncHarness.Row("222", OctorateMappingStatus.Confirmed, PropertyA2),
                    MessageSyncHarness.Row("444", OctorateMappingStatus.Proposed, null),
                ];
            }

            return org == OrgB ? [MessageSyncHarness.Row("333", OctorateMappingStatus.Confirmed, PropertyB1)] : [];
        }

        private static OctorateConnection Connection(Guid orgId, OctorateConnectionStatus status) =>
            new() { OrgId = orgId, Environment = OctorateEnvironment.Test, Status = status };
    }
}
