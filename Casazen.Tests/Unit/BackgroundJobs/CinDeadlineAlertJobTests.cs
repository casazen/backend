using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// CO-20 (A5-31): the daily CIN alert job only runs the alert service (which decides and deduplicates), after the nightly
/// compliance check of CO-06. The alert itself is tested on PostgreSQL in <c>CinDeadlineAlertsPostgresTests</c>.
/// </summary>
public class CinDeadlineAlertJobTests
{
    [Fact]
    public void Configure_CinDeadlineAlert_IsDailyAfterTheComplianceCheck()
    {
        var registered = new Dictionary<string, (Job Job, string Cron)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, _) => registered[id] = (job, cron));

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        var alert = registered["cin-deadline-alert"];
        Assert.Equal(typeof(CinDeadlineAlertJob), alert.Job.Type);
        Assert.Equal("0 8 * * *", alert.Cron);
        Assert.Equal("0 4 * * *", registered[PropertyComplianceCheckJob.RecurringJobId].Cron);
    }

    [Fact]
    public async Task ExecuteAsync_RunsTheAlertServiceOnce()
    {
        var service = new Mock<ICinDeadlineAlertService>();
        service
            .Setup(s => s.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CinDeadlineAlertRunResult(Skipped: false, Stage: null, PropertiesAlerted: 0, EmailsQueued: 0));

        await new CinDeadlineAlertJob(service.Object).ExecuteAsync();

        service.Verify(s => s.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ExecuteAsync_NeverOverlapsAnotherRun()
    {
        var method = typeof(CinDeadlineAlertJob).GetMethod(nameof(CinDeadlineAlertJob.ExecuteAsync))!;

        Assert.NotNull(method.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false).SingleOrDefault());
    }
}
