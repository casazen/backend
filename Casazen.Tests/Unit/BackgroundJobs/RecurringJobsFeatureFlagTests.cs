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

    [Fact]
    public void Configure_ESignProviderOff_DoesNotWatchTheProviderAndRemovesTheOldSchedule()
    {
        var (manager, registered) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false));

        Assert.Equal("lease-sign-status-poll", LeaseSignStatusPollingJob.RecurringJobId);
        Assert.DoesNotContain(LeaseSignStatusPollingJob.RecurringJobId, registered);
        manager.Verify(m => m.RemoveIfExists(LeaseSignStatusPollingJob.RecurringJobId), Times.Once);
    }

    [Fact]
    public void Configure_ESignProviderOn_WatchesTheProvider()
    {
        var (manager, registered) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false, eSignProvider: true));

        Assert.Contains(LeaseSignStatusPollingJob.RecurringJobId, registered);
        manager.Verify(m => m.RemoveIfExists(LeaseSignStatusPollingJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void Configure_SupplierRequestAutoCancelOff_DoesNotScheduleTheJobAndRemovesTheScheduleOfAnEarlierDeploy()
    {
        var (manager, registered) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false));

        Assert.Equal("service-request-auto-cancel", ServiceRequestAutoCancelJob.RecurringJobId);
        Assert.DoesNotContain(ServiceRequestAutoCancelJob.RecurringJobId, registered);
        manager.Verify(m => m.RemoveIfExists(ServiceRequestAutoCancelJob.RecurringJobId), Times.Once);
        Assert.Contains("ical-supplier-sync", registered);
    }

    [Fact]
    public void Configure_SupplierRequestAutoCancelOn_SchedulesTheJobEveryTenMinutesInUtc()
    {
        var manager = new Mock<IRecurringJobManager>();
        var scheduled = new Dictionary<string, (string Cron, RecurringJobOptions Options)>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, _, cron, options) => scheduled[id] = (cron, options));

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false, supplierRequestAutoCancel: true));

        var job = scheduled[ServiceRequestAutoCancelJob.RecurringJobId];
        Assert.Equal("*/10 * * * *", job.Cron);
        Assert.Equal(TimeZoneInfo.Utc, job.Options.TimeZone);
        manager.Verify(m => m.RemoveIfExists(ServiceRequestAutoCancelJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void Configure_OtherFlagsOn_DoNotTurnTheAutoCancelOn()
    {
        var (manager, registered) = Manager();

        RecurringJobsRegistration.Configure(
            manager.Object, Flags(otaPartnerApi: true, rliProvider: true, eSignProvider: true));

        Assert.DoesNotContain(ServiceRequestAutoCancelJob.RecurringJobId, registered);
    }

    [Fact]
    public void Configure_TheDraftRetention_IsAlwaysScheduledDailyAt0350Utc_AndNeverRemoved()
    {
        var manager = new Mock<IRecurringJobManager>();
        var scheduled = new Dictionary<string, (string Cron, RecurringJobOptions Options)>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, _, cron, options) => scheduled[id] = (cron, options));

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false));

        Assert.Equal("lease-draft-retention", LeaseDraftRetentionJob.RecurringJobId);
        var job = scheduled[LeaseDraftRetentionJob.RecurringJobId];
        Assert.Equal("50 3 * * *", job.Cron);
        Assert.Equal(TimeZoneInfo.Utc, job.Options.TimeZone);
        manager.Verify(m => m.RemoveIfExists(LeaseDraftRetentionJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void Configure_TheShowcaseBookingUpkeep_IsRegisteredWhateverTheFlagsSay_AndNeverRemoved()
    {
        Assert.Equal("service-request-expiry", ServiceRequestExpiryJob.RecurringJobId);
        Assert.Equal("service-request-reminders", ServiceRequestReminderJob.RecurringJobId);

        foreach (var flags in new[]
                 {
                     Flags(otaPartnerApi: false),
                     Flags(otaPartnerApi: true, rliProvider: true, eSignProvider: true, supplierRequestAutoCancel: true),
                 })
        {
            var (manager, registered) = Manager();

            RecurringJobsRegistration.Configure(manager.Object, flags);

            Assert.Contains(ServiceRequestExpiryJob.RecurringJobId, registered);
            Assert.Contains(ServiceRequestReminderJob.RecurringJobId, registered);
            manager.Verify(m => m.RemoveIfExists(ServiceRequestExpiryJob.RecurringJobId), Times.Never);
            manager.Verify(m => m.RemoveIfExists(ServiceRequestReminderJob.RecurringJobId), Times.Never);
        }
    }

    [Fact]
    public void Configure_TheShowcaseBookingUpkeep_RunsEveryFiveMinutes_AndTheRemindersEveryHour_InUtc()
    {
        var manager = new Mock<IRecurringJobManager>();
        var scheduled = new Dictionary<string, (string Cron, RecurringJobOptions Options)>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, _, cron, options) => scheduled[id] = (cron, options));

        RecurringJobsRegistration.Configure(manager.Object, Flags(otaPartnerApi: false));

        Assert.Equal("comune-official-profile-refresh", ComuneOfficialProfileRefreshJob.RecurringJobId);
        Assert.Equal("0 5 1 * *", scheduled[ComuneOfficialProfileRefreshJob.RecurringJobId].Cron);
        Assert.Equal(TimeZoneInfo.Utc, scheduled[ComuneOfficialProfileRefreshJob.RecurringJobId].Options.TimeZone);
        Assert.Contains(OfficialReferenceDataRefreshJob.RecurringJobId, scheduled.Keys);
        Assert.Equal("*/5 * * * *", scheduled[ServiceRequestExpiryJob.RecurringJobId].Cron);
        Assert.Equal("0 * * * *", scheduled[ServiceRequestReminderJob.RecurringJobId].Cron);
        Assert.Equal(TimeZoneInfo.Utc, scheduled[ServiceRequestExpiryJob.RecurringJobId].Options.TimeZone);
        Assert.Equal(TimeZoneInfo.Utc, scheduled[ServiceRequestReminderJob.RecurringJobId].Options.TimeZone);
    }

    [Fact]
    public void Configure_TheBackupGroupsJob_IsRegisteredWhateverTheFlagsSay_EveryFiveMinutes_AndNeverRemoved()
    {
        Assert.Equal("service-request-groups", ServiceRequestGroupJob.RecurringJobId);

        foreach (var flags in new[]
                 {
                     Flags(otaPartnerApi: false),
                     Flags(otaPartnerApi: true, rliProvider: true, eSignProvider: true, supplierRequestAutoCancel: true),
                 })
        {
            var manager = new Mock<IRecurringJobManager>();
            var scheduled = new Dictionary<string, (string Cron, RecurringJobOptions Options)>();
            manager
                .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
                .Callback<string, Job, string, RecurringJobOptions>((id, _, cron, options) => scheduled[id] = (cron, options));

            RecurringJobsRegistration.Configure(manager.Object, flags);

            Assert.Equal("*/5 * * * *", scheduled[ServiceRequestGroupJob.RecurringJobId].Cron);
            Assert.Equal(TimeZoneInfo.Utc, scheduled[ServiceRequestGroupJob.RecurringJobId].Options.TimeZone);
            manager.Verify(m => m.RemoveIfExists(ServiceRequestGroupJob.RecurringJobId), Times.Never);
        }
    }

    internal static IFeatureFlags Flags(
        bool otaPartnerApi,
        bool rliProvider = false,
        bool eSignProvider = false,
        bool supplierRequestAutoCancel = false,
        bool supplierOnlinePayments = false,
        bool accountantPackage = false)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.AccountantPackage)).Returns(accountantPackage);
        flags.Setup(f => f.IsEnabled(FeatureFlags.OtaPartnerApi)).Returns(otaPartnerApi);
        flags.Setup(f => f.IsEnabled(FeatureFlags.RliProvider)).Returns(rliProvider);
        flags.Setup(f => f.IsEnabled(FeatureFlags.ESignProvider)).Returns(eSignProvider);
        flags.Setup(f => f.IsEnabled(FeatureFlags.SupplierRequestAutoCancel)).Returns(supplierRequestAutoCancel);
        flags.Setup(f => f.IsEnabled(FeatureFlags.SupplierOnlinePayments)).Returns(supplierOnlinePayments);
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
