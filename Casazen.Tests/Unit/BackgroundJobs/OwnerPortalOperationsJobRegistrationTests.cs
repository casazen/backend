using System.Reflection;
using Casazen.Core.Features;
using Casazen.Infrastructure.Jobs;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class OwnerPortalOperationsJobRegistrationTests
{
    [Fact]
    public void Configure_AgencyTierOff_DoesNotScheduleTheJob_AndRemovesTheScheduleOfAnEarlierDeploy()
    {
        var (manager, scheduled) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(agencyTier: false));

        Assert.Equal("owner-portal-operations", OwnerPortalOperationsJob.RecurringJobId);
        Assert.DoesNotContain(OwnerPortalOperationsJob.RecurringJobId, scheduled.Keys);
        manager.Verify(m => m.RemoveIfExists(OwnerPortalOperationsJob.RecurringJobId), Times.Once);
    }

    [Fact]
    public void Configure_AgencyTierOn_SchedulesTheJobEveryFiveMinutes_AndItNeverRunsTwiceAtOnce()
    {
        var (manager, scheduled) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(agencyTier: true));

        var job = scheduled[OwnerPortalOperationsJob.RecurringJobId];
        Assert.Equal(("*/5 * * * *", TimeZoneInfo.Utc), (job.Cron, job.Options.TimeZone));
        Assert.Equal(typeof(OwnerPortalOperationsJob), job.Job.Type);
        manager.Verify(m => m.RemoveIfExists(OwnerPortalOperationsJob.RecurringJobId), Times.Never);
        foreach (var name in new[] { nameof(OwnerPortalOperationsJob.ExecuteAsync), nameof(OwnerPortalOperationsJob.ProcessStayAsync) })
            Assert.NotNull(typeof(OwnerPortalOperationsJob).GetMethod(name)!.GetCustomAttribute<DisableConcurrentExecutionAttribute>());
    }

    private static IFeatureFlags Flags(bool agencyTier)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.AgencyTier)).Returns(agencyTier);
        return flags.Object;
    }

    private static (Mock<IRecurringJobManager> Manager, Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Scheduled) Manager()
    {
        var scheduled = new Dictionary<string, (Job, string, RecurringJobOptions)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => scheduled[id] = (job, cron, options));
        return (manager, scheduled);
    }
}
