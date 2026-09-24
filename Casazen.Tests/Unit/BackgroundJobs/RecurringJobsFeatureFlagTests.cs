using Casazen.Core.Features;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// FD-20 (A2-09, A9-15): the OTA partner jobs <c>ota-sync-all</c> / <c>booking-pull-all</c> ran every hour / 15 minutes
/// with <c>Guid.Empty</c> and logged a warning each time. With <c>Features:OtaPartnerApi</c> off they are not registered
/// and the schedule left by an earlier deploy is removed; the iCal sync is not affected.
/// </summary>
public class RecurringJobsFeatureFlagTests
{
    private static readonly string[] OtaJobIds =
    [
        RecurringJobsRegistration.OtaSyncAllJobId,
        RecurringJobsRegistration.BookingPullAllJobId,
    ];

    [Fact]
    public void Configure_OtaPartnerApiOff_DoesNotRegisterOtaJobs()
    {
        var (manager, registered) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false));

        Assert.Equal("ota-sync-all", RecurringJobsRegistration.OtaSyncAllJobId);
        Assert.Equal("booking-pull-all", RecurringJobsRegistration.BookingPullAllJobId);
        Assert.All(OtaJobIds, id => Assert.DoesNotContain(id, registered));
        Assert.Contains("property-ical-sync", registered);
        Assert.Contains("ical-supplier-sync", registered);
    }

    [Fact]
    public void Configure_OtaPartnerApiOff_RemovesOtaJobsOfEarlierDeploys()
    {
        var (manager, _) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false));

        foreach (var id in OtaJobIds)
            manager.Verify(m => m.RemoveIfExists(id), Times.Once);
    }

    [Fact]
    public void Configure_OtaPartnerApiOn_RegistersOtaJobs()
    {
        var (manager, registered) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: true));

        Assert.All(OtaJobIds, id => Assert.Contains(id, registered));
        foreach (var id in OtaJobIds)
            manager.Verify(m => m.RemoveIfExists(id), Times.Never);
    }

    [Fact]
    public void Configure_RliProviderOff_DoesNotPollTheProviderAndRemovesTheOldSchedule()
    {
        // LT-01: with the provider path off every lease is registered manually; nothing to poll.
        var (manager, registered) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false));

        Assert.Equal("lease-registration-status-poll", LeaseRegistrationStatusPollingJob.RecurringJobId);
        Assert.DoesNotContain(LeaseRegistrationStatusPollingJob.RecurringJobId, registered);
        manager.Verify(m => m.RemoveIfExists(LeaseRegistrationStatusPollingJob.RecurringJobId), Times.Once);
        Assert.Contains("rli-deadline-reminder", registered);
    }

    [Fact]
    public void Configure_RliProviderOn_PollsTheProvider()
    {
        var (manager, registered) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false, rliProvider: true));

        Assert.Contains(LeaseRegistrationStatusPollingJob.RecurringJobId, registered);
        manager.Verify(m => m.RemoveIfExists(LeaseRegistrationStatusPollingJob.RecurringJobId), Times.Never);
    }

    internal static IFeatureFlags Flags(bool otaPartnerApi, bool rliProvider = false)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OtaPartnerApi)).Returns(otaPartnerApi);
        flags.Setup(f => f.IsEnabled(FeatureFlags.RliProvider)).Returns(rliProvider);
        return flags.Object;
    }

    private static (Mock<IRecurringJobManager> Manager, List<string> Registered) Manager()
    {
        var registered = new List<string>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, _, _, _) => registered.Add(id));
        return (manager, registered);
    }
}
