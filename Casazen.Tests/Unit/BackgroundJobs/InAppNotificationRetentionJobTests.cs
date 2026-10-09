using System.Reflection;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// UI-12a: the in-app notifications older than 90 days are deleted every night, whatever the state of the flag (the deletion
/// itself, on PostgreSQL, is proved in <c>InAppNotificationsPostgresTests</c>).
/// </summary>
public class InAppNotificationRetentionJobTests
{
    private static (Mock<IRecurringJobManager> Manager, Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Scheduled) Register(
        bool anyFlagOn)
    {
        var scheduled = new Dictionary<string, (Job, string, RecurringJobOptions)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => scheduled[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(
            manager.Object,
            RecurringJobsFeatureFlagTests.Flags(
                otaPartnerApi: anyFlagOn,
                rliProvider: anyFlagOn,
                eSignProvider: anyFlagOn,
                supplierRequestAutoCancel: anyFlagOn,
                supplierOnlinePayments: anyFlagOn,
                propertyModeChange: anyFlagOn));
        return (manager, scheduled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configure_IsRegisteredDailyInUtc_WhateverTheFlagsSay_AndNeverRemoved(bool anyFlagOn)
    {
        var (manager, scheduled) = Register(anyFlagOn);

        Assert.Equal("in-app-notification-retention", InAppNotificationRetentionJob.RecurringJobId);
        var registered = scheduled[InAppNotificationRetentionJob.RecurringJobId];
        Assert.Equal("45 3 * * *", registered.Cron);
        Assert.Equal(TimeZoneInfo.Utc, registered.Options.TimeZone);
        Assert.Equal(typeof(InAppNotificationRetentionJob), registered.Job.Type);
        manager.Verify(m => m.RemoveIfExists(InAppNotificationRetentionJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void ExecuteAsync_NeverRunsTwiceAtOnce_WithinTheDefaultLockTimeout()
    {
        var method = typeof(InAppNotificationRetentionJob).GetMethod(nameof(InAppNotificationRetentionJob.ExecuteAsync))!;

        var concurrency = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.Equal(JobLockTimeouts.DefaultSeconds, concurrency.TimeoutSec);
    }

    [Fact]
    public void Cron_DoesNotShareTheMinuteOfTheOtherNightlyRetentions()
    {
        // 03:00 GDPR retention, 03:30 SEO events: the three do not start together.
        Assert.NotEqual("0 3 * * *", InAppNotificationRetentionJob.Cron);
        Assert.NotEqual("30 3 * * *", InAppNotificationRetentionJob.Cron);
    }

    [Fact]
    public async Task ExecuteAsync_DeletesWhatTheServiceSaysIsExpired()
    {
        var service = new Mock<IInAppNotificationService>(MockBehavior.Strict);
        service.Setup(s => s.PurgeExpiredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(12);
        var job = new InAppNotificationRetentionJob(service.Object, NullLogger<InAppNotificationRetentionJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        service.Verify(s => s.PurgeExpiredAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
