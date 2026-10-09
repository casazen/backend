using System.Reflection;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// SP-15b: the jobs of the supplier service payments. The sync and the reminders are always on the schedule, whatever the state of
/// the feature flag <c>SupplierOnlinePayments</c> (they follow money that is already in flight, and the reminders only flag the late
/// payments while the flag is off: the jobs read the flag themselves, <c>SupplierPaymentJobsTests</c>); the third job is queued when
/// <c>account.updated</c> says a supplier has just become ready. Each one holds a lock so two runs never overlap.
/// </summary>
public class ServicePaymentJobsRegistrationTests
{
    private static (Dictionary<string, (string Cron, Job Job, RecurringJobOptions Options)> Scheduled, Mock<IRecurringJobManager> Manager) Register(
        bool supplierOnlinePayments = false)
    {
        var scheduled = new Dictionary<string, (string, Job, RecurringJobOptions)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => scheduled[id] = (cron, job, options));

        RecurringJobsRegistration.Configure(
            manager.Object,
            RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false, supplierOnlinePayments: supplierOnlinePayments));

        return (scheduled, manager);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configure_TheSyncAndTheReminders_AreScheduledWhateverTheFlag(bool supplierOnlinePayments)
    {
        var (scheduled, manager) = Register(supplierOnlinePayments);

        Assert.Equal("service-payment-sync", ServicePaymentSyncJob.RecurringJobId);
        Assert.Equal("service-payment-reminders", ServicePaymentRemindersJob.RecurringJobId);
        Assert.Contains(ServicePaymentSyncJob.RecurringJobId, scheduled.Keys);
        Assert.Contains(ServicePaymentRemindersJob.RecurringJobId, scheduled.Keys);
        // They are never removed from the schedule: with the flag off they still follow the payments and the refunds in flight.
        manager.Verify(m => m.RemoveIfExists(ServicePaymentSyncJob.RecurringJobId), Times.Never);
        manager.Verify(m => m.RemoveIfExists(ServicePaymentRemindersJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void Configure_TheSync_RunsEveryFifteenMinutes_InUtc()
    {
        var (scheduled, _) = Register();

        var sync = scheduled[ServicePaymentSyncJob.RecurringJobId];
        Assert.Equal("*/15 * * * *", sync.Cron);
        Assert.Equal(TimeZoneInfo.Utc, sync.Options.TimeZone);
        Assert.Equal(typeof(ServicePaymentSyncJob), sync.Job.Type);
        Assert.Equal(nameof(ServicePaymentSyncJob.ExecuteAsync), sync.Job.Method.Name);
    }

    [Fact]
    public void Configure_TheReminders_RunEveryDayAtHalfPastSevenUtc()
    {
        var (scheduled, _) = Register();

        var reminders = scheduled[ServicePaymentRemindersJob.RecurringJobId];
        Assert.Equal("30 7 * * *", reminders.Cron);
        Assert.Equal(TimeZoneInfo.Utc, reminders.Options.TimeZone);
        Assert.Equal(typeof(ServicePaymentRemindersJob), reminders.Job.Type);
    }

    [Fact]
    public void TheSync_WaitsLessThanItsInterval_ForThePreviousRun()
    {
        var attribute = ConcurrencyAttribute(typeof(ServicePaymentSyncJob).GetMethod(nameof(ServicePaymentSyncJob.ExecuteAsync))!);

        Assert.NotNull(attribute);
        Assert.Equal(JobLockTimeouts.FrequentSeconds, attribute.TimeoutSec);
        Assert.True(attribute.TimeoutSec < 15 * 60);
    }

    [Theory]
    [InlineData(typeof(ServicePaymentRemindersJob))]
    [InlineData(typeof(SendPendingPaymentRequestsJob))]
    public void TheEmailJobs_HoldTheirLock_AndRetryAFewTimes(Type job)
    {
        var method = job.GetMethod("ExecuteAsync")!;

        var concurrency = ConcurrencyAttribute(method);
        Assert.NotNull(concurrency);
        Assert.InRange(concurrency.TimeoutSec, 1, (int)HangfireStorageSettings.DefaultDistributedLockTimeout.TotalSeconds);
        // A failed run is tried again a few times: the payer is only ever emailed once, whatever the number of runs.
        var retry = method.GetCustomAttribute<AutomaticRetryAttribute>();
        Assert.NotNull(retry);
        Assert.Equal(3, retry.Attempts);
    }

    [Fact]
    public async Task TheSync_CallsTheService_AndStaysQuietWhenThereIsNothingToDo()
    {
        var service = new Mock<ISupplierPaymentJobService>();
        service.Setup(s => s.SynchronizeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ServicePaymentSyncRun(0, 0, 0, 0, 0));

        await new ServicePaymentSyncJob(service.Object, NullLogger<ServicePaymentSyncJob>.Instance).ExecuteAsync(CancellationToken.None);

        service.Verify(s => s.SynchronizeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheReminders_CallTheService()
    {
        var service = new Mock<ISupplierPaymentJobService>();
        service
            .Setup(s => s.RunRemindersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServicePaymentReminderRun(true, 1, 2, 3, 0, 0));

        await new ServicePaymentRemindersJob(service.Object, NullLogger<ServicePaymentRemindersJob>.Instance).ExecuteAsync(CancellationToken.None);

        service.Verify(s => s.RunRemindersAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ThePendingRequestsJob_SendsTheRequestsOfTheSupplier_ItWasQueuedFor()
    {
        var supplier = Guid.NewGuid();
        var service = new Mock<ISupplierPaymentJobService>();
        service.Setup(s => s.SendPendingRequestsAsync(supplier, It.IsAny<CancellationToken>())).ReturnsAsync(2);

        await new SendPendingPaymentRequestsJob(service.Object).ExecuteAsync(supplier, CancellationToken.None);

        service.Verify(s => s.SendPendingRequestsAsync(supplier, It.IsAny<CancellationToken>()), Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public void TheScheduler_QueuesThePendingRequestsJob_ForTheSupplier()
    {
        var supplier = Guid.NewGuid();
        var queued = new List<(Job Job, IState State)>();
        var client = new Mock<IBackgroundJobClient>();
        client
            .Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, state) => queued.Add((job, state)))
            .Returns("1");

        new SupplierPaymentJobScheduler(client.Object, NullLogger<SupplierPaymentJobScheduler>.Instance).SchedulePendingRequests(supplier);

        var (job, state) = Assert.Single(queued);
        Assert.Equal(typeof(SendPendingPaymentRequestsJob), job.Type);
        Assert.Equal(nameof(SendPendingPaymentRequestsJob.ExecuteAsync), job.Method.Name);
        Assert.Equal(supplier, job.Args[0]);
        // Right away, not scheduled for later.
        Assert.IsType<EnqueuedState>(state);
    }

    [Fact]
    public void TheScheduler_WhenTheQueueFails_DoesNotFailTheEvent_TheDailyJobMakesUpForIt()
    {
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("storage down"));

        var exception = Record.Exception(
            () => new SupplierPaymentJobScheduler(client.Object, NullLogger<SupplierPaymentJobScheduler>.Instance).SchedulePendingRequests(Guid.NewGuid()));

        Assert.Null(exception);
    }

    private static DisableConcurrentExecutionAttribute? ConcurrencyAttribute(MethodInfo method) =>
        method.GetCustomAttribute<DisableConcurrentExecutionAttribute>()
        ?? method.DeclaringType?.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
}
