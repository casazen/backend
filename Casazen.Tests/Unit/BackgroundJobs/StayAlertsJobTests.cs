using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>CO-10: one hourly job for the stay alerts in place of the hourly Alloggiati alert and the daily reminder.</summary>
public class StayAlertsJobTests
{
    [Fact]
    public void Configure_StayAlerts_IsHourlyAndReplacesTheOldAlertJobs()
    {
        var registered = new Dictionary<string, (Job Job, string Cron)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, _) => registered[id] = (job, cron));

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        var stayAlerts = registered[StayAlertsJob.RecurringJobId];
        Assert.Equal("stay-alerts", StayAlertsJob.RecurringJobId);
        Assert.Equal(typeof(StayAlertsJob), stayAlerts.Job.Type);
        Assert.Equal(Cron.Hourly(), stayAlerts.Cron);
        Assert.DoesNotContain("alloggiati-deadline-alert", registered.Keys);
        Assert.DoesNotContain("guest-checkin-reminder", registered.Keys);
        manager.Verify(m => m.RemoveIfExists("alloggiati-deadline-alert"), Times.Once);
        manager.Verify(m => m.RemoveIfExists("guest-checkin-reminder"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RunsTheStayAlertsOnce()
    {
        var service = new Mock<IStayAlertService>();
        service.Setup(s => s.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new StayAlertRunResult(false, 0));

        await new StayAlertsJob(service.Object).ExecuteAsync();

        service.Verify(s => s.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
