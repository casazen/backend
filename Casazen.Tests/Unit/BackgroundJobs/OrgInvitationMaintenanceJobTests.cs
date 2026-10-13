using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// AM-02: the hourly job of the org invitations only runs the maintenance service (which decides, locks and deduplicates).
/// It is registered whatever the <c>OrgTeam</c> flag says, because the retention of the personal data of an invitation must
/// not depend on a feature being on, and it never overlaps another run.
/// </summary>
public class OrgInvitationMaintenanceJobTests
{
    private static (IReadOnlyDictionary<string, (Job Job, string Cron)> Registered, Mock<IRecurringJobManager> Manager) Register(bool orgTeam)
    {
        var registered = new Dictionary<string, (Job Job, string Cron)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, _) => registered[id] = (job, cron));

        var flags = new Mock<Casazen.Core.Features.IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(Casazen.Core.Features.FeatureFlags.OrgTeam)).Returns(orgTeam);
        RecurringJobsRegistration.Configure(manager.Object, flags.Object);
        return (registered, manager);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configure_RegistersTheJobHourly_WhateverTheOrgTeamFlag(bool orgTeam)
    {
        var (registered, manager) = Register(orgTeam);

        var job = registered["org-invitation-maintenance"];
        Assert.Equal(typeof(OrgInvitationMaintenanceJob), job.Job.Type);
        Assert.Equal("10 * * * *", job.Cron);
        manager.Verify(m => m.RemoveIfExists("org-invitation-maintenance"), Times.Never);
    }

    [Fact]
    public void RecurringJobId_IsStable()
    {
        Assert.Equal("org-invitation-maintenance", OrgInvitationMaintenanceJob.RecurringJobId);
    }

    [Fact]
    public async Task ExecuteAsync_RunsTheMaintenanceOnce()
    {
        var service = new Mock<IOrgInvitationMaintenanceService>();
        service.Setup(s => s.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OrgInvitationMaintenanceResult(false, 1, 2, 3));

        await new OrgInvitationMaintenanceJob(service.Object).ExecuteAsync();

        service.Verify(s => s.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ExecuteAsync_NeverOverlapsAnotherRun()
    {
        var method = typeof(OrgInvitationMaintenanceJob).GetMethod(nameof(OrgInvitationMaintenanceJob.ExecuteAsync))!;

        var attribute = Assert.IsType<DisableConcurrentExecutionAttribute>(
            Assert.Single(method.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false)));
        Assert.InRange(attribute.TimeoutSec, 1, 300);
    }
}
