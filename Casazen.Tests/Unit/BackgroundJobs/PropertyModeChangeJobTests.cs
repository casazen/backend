using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// PM-02 (D16): the hourly <c>property-mode-change</c> job only runs the mode change service, which decides what is due and
/// is idempotent. It is always registered, hourly so a change is applied within the hour after midnight of Rome, and never
/// overlaps itself.
/// </summary>
public class PropertyModeChangeJobTests
{
    [Fact]
    public void Configure_RegistersTheJobHourlyInUtc()
    {
        var registered = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => registered[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        var entry = registered["property-mode-change"];
        Assert.Equal("property-mode-change", PropertyModeChangeJob.RecurringJobId);
        Assert.Equal(typeof(PropertyModeChangeJob), entry.Job.Type);
        Assert.Equal(nameof(PropertyModeChangeJob.ExecuteAsync), entry.Job.Method.Name);
        Assert.Equal("0 * * * *", entry.Cron);
        Assert.Equal(Cron.Hourly(), entry.Cron);
        Assert.Equal(TimeZoneInfo.Utc, entry.Options.TimeZone);
        manager.Verify(m => m.RemoveIfExists(PropertyModeChangeJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RunsTheModeChangeServiceOnce()
    {
        var service = new Mock<IPropertyModeService>();
        service
            .Setup(s => s.ApplyDueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PropertyModeRunResult(Skipped: false, Examined: 2, Applied: 2, Failed: 0));
        using var cts = new CancellationTokenSource();

        await new PropertyModeChangeJob(service.Object).ExecuteAsync(cts.Token);

        service.Verify(s => s.ApplyDueAsync(cts.Token), Times.Once);
    }

    [Fact]
    public void ExecuteAsync_NeverOverlapsAnotherRun()
    {
        var method = typeof(PropertyModeChangeJob).GetMethod(nameof(PropertyModeChangeJob.ExecuteAsync))!;

        var attribute = Assert.IsType<DisableConcurrentExecutionAttribute>(
            Assert.Single(method.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false)));
        Assert.Equal(JobLockTimeouts.DefaultSeconds, attribute.TimeoutSec);
    }
}
