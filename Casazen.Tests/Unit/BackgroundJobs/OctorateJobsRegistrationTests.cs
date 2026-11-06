using System.Reflection;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// The recurring jobs of the Octorate connector (epic E02, ADR-031, ADR-033, ADR-002): scheduled only while <c>Features:OctorateConnector</c> is
/// on, removed (<c>RemoveIfExists</c>) while it is off so that the schedule of an earlier deploy stops, each with the lock that keeps two runs
/// from overlapping (FD-11) and the lock wait of its kind.
/// </summary>
public class OctorateJobsRegistrationTests
{
    private static readonly string[] OctorateJobIds =
    [
        OctorateBookingImportJob.RecurringJobId,
        OctorateBookingSyncJob.RecurringJobId,
        CalendarSyncJob.RecurringJobId,
        ListingStatusSyncJob.RecurringJobId,
        ListingChannelHealthJob.RecurringJobId,
        ListingOutsideChangeJob.RecurringJobId,
    ];

    [Fact]
    public void Configure_OctorateConnectorOn_SchedulesTheBookingImportDailyAt0200InRome()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(octorateConnector: true));

        Assert.Equal("octorate-booking-import", OctorateBookingImportJob.RecurringJobId);
        var import = jobs[OctorateBookingImportJob.RecurringJobId];
        Assert.Equal("0 2 * * *", import.Cron);
        Assert.Equal(RomeCalendar.TimeZone, import.Options.TimeZone);
        Assert.Equal(nameof(OctorateBookingImportJob.ExecuteAsync), import.Job.Method.Name);
        manager.Verify(m => m.RemoveIfExists(OctorateBookingImportJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void Configure_OctorateConnectorOn_SchedulesTheReconciliationEverySixHours()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(octorateConnector: true));

        Assert.Equal("octorate-booking-sync", OctorateBookingSyncJob.RecurringJobId);
        var sync = jobs[OctorateBookingSyncJob.RecurringJobId];
        Assert.Equal("0 */6 * * *", sync.Cron);
        Assert.Equal(TimeZoneInfo.Utc, sync.Options.TimeZone);
        Assert.Equal(nameof(OctorateBookingSyncJob.ExecuteAsync), sync.Job.Method.Name);
        Assert.Equal(TimeSpan.FromMinutes(5), OctorateBookingSyncJob.OptimisticWindow);
        manager.Verify(m => m.RemoveIfExists(OctorateBookingSyncJob.RecurringJobId), Times.Never);
    }

    [Fact]
    public void Configure_OctorateConnectorOn_SchedulesTheCalendarSyncEveryFifteenMinutes_NextToTheICalSyncThatKeepsItsOwn()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(octorateConnector: true));

        // ADR-002-R2, NF-36: the new job runs every 15 minutes, like the iCal sync of the feeds, which is registered as it was.
        Assert.Equal("CalendarSync", CalendarSyncJob.RecurringJobId);
        var calendar = jobs[CalendarSyncJob.RecurringJobId];
        Assert.Equal("*/15 * * * *", calendar.Cron);
        Assert.Equal(TimeZoneInfo.Utc, calendar.Options.TimeZone);
        Assert.Equal(nameof(CalendarSyncJob.ExecuteAsync), calendar.Job.Method.Name);
        manager.Verify(m => m.RemoveIfExists(CalendarSyncJob.RecurringJobId), Times.Never);

        var ical = jobs["property-ical-sync"];
        Assert.Equal(PropertyICalSchedule.SyncCron, ical.Cron);
        Assert.Equal(calendar.Cron, ical.Cron);
        Assert.Equal(15, PropertyICalSchedule.SyncEveryMinutes);
        Assert.Contains("ical-supplier-sync", jobs.Keys);
    }

    [Fact]
    public void Configure_OctorateConnectorOff_SchedulesNothingAndRemovesTheScheduleOfAnEarlierDeploy()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(octorateConnector: false));

        Assert.All(OctorateJobIds, id =>
        {
            Assert.DoesNotContain(id, jobs.Keys);
            manager.Verify(m => m.RemoveIfExists(id), Times.Once);
        });
        // The rest of the schedule is not touched by the flag.
        Assert.Contains("property-ical-sync", jobs.Keys);
    }

    [Fact]
    public void Configure_EveryOctorateJob_HoldsALockSoTwoRunsNeverOverlap_AndWaitsForItAsLongAsItsKindOfJob()
    {
        var (manager, jobs) = Manager();

        RecurringJobsRegistration.Configure(manager.Object, Flags(octorateConnector: true));

        Assert.All(OctorateJobIds, id =>
        {
            var method = jobs[id].Job.Method;
            var attribute = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>()
                ?? method.DeclaringType?.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
            Assert.True(attribute is not null, $"Recurring job '{id}' has no [DisableConcurrentExecution]");
            Assert.InRange(attribute.TimeoutSec, 1, (int)HangfireStorageSettings.DefaultDistributedLockTimeout.TotalSeconds);
        });
    }

    [Fact]
    public void TheLockWaitOfTheJobs_IsTheOneOfTheWebJobsOfTheSameKind()
    {
        // The jobs live in the infrastructure project, which cannot see JobLockTimeouts (web): the numbers are kept equal here.
        Assert.Equal(JobLockTimeouts.DefaultSeconds, OctorateBookingImportJob.LockTimeoutSeconds);
        Assert.Equal(JobLockTimeouts.DefaultSeconds, OctorateBookingSyncJob.LockTimeoutSeconds);

        // The calendar sync runs every 15 minutes: it waits as the other frequent jobs do, far less than its interval.
        Assert.Equal(JobLockTimeouts.FrequentSeconds, CalendarSyncJob.LockTimeoutSeconds);
        Assert.True(CalendarSyncJob.LockTimeoutSeconds < 5 * 60);

        // The follow-up of the publications is as frequent; the check of the changes made outside CasaZen, the daily health check and the
        // publication of a listing, run on demand, wait as the other hourly, daily and on-demand jobs do.
        Assert.Equal(JobLockTimeouts.FrequentSeconds, ListingStatusSyncJob.LockTimeoutSeconds);
        Assert.Equal(JobLockTimeouts.DefaultSeconds, ListingOutsideChangeJob.LockTimeoutSeconds);
        Assert.Equal(JobLockTimeouts.DefaultSeconds, ListingChannelHealthJob.LockTimeoutSeconds);
        Assert.Equal(JobLockTimeouts.DefaultSeconds, ListingPublishJob.LockTimeoutSeconds);
    }

    [Fact]
    public void TheJobClassesOfTheConnector_AreRegisteredInTheContainer()
    {
        // AddCasazenOctorate registers the classes in every case ("what is not ready sits behind its flag where it is used").
        var services = new ServiceCollection();
        services.AddCasazenOctorate(new ConfigurationBuilder().Build());

        foreach (var type in new[]
                 {
                     typeof(OctorateBookingImportJob),
                     typeof(OctorateBookingSyncJob),
                     typeof(OctorateBookingStatePushJob),
                     typeof(CalendarSyncJob),
                     typeof(ICalMigrationService),
                     typeof(ListingPublishJob),
                     typeof(ListingStatusSyncJob),
                     typeof(ListingChannelHealthJob),
                     typeof(ListingOutsideChangeJob),
                 })
        {
            Assert.Contains(services, d => d.ServiceType == type && d.Lifetime == ServiceLifetime.Scoped);
        }
    }

    [Fact]
    public void TheEventHandlersOfTheBookings_AreRegisteredOnTheBus()
    {
        var services = new ServiceCollection();
        services.AddCasazenOctorate(new ConfigurationBuilder().Build());

        // One handler for each kind of notification of a reservation...
        var notifications = services
            .Where(d => d.ServiceType == typeof(IEventHandler<OctorateWebhookReceivedEvent>))
            .Select(d => d.ImplementationType)
            .ToList();
        Assert.Equal(
            [typeof(BookingCancelledHandler), typeof(BookingCreatedHandler), typeof(BookingModifiedHandler), typeof(ContentNotificationHandler)],
            notifications.OrderBy(t => t!.Name, StringComparer.Ordinal));

        // ...and the one that queues the push of the arrival and the departure.
        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IEventHandler<DomainEvent>) && d.ImplementationType == typeof(OctorateBookingStatePushHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(IBookingImportService) && d.ImplementationType == typeof(BookingImportService));
        Assert.Contains(services, d => d.ServiceType == typeof(BookingStateMapper));
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
}
