using Casazen.Core.Options;
using Casazen.Infrastructure.Jobs;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    private static void RegisterEventJobs(IRecurringJobManager recurringJobManager, RoutineOptions? routineOptions)
    {
        recurringJobManager.AddOrUpdate<TimeEventEmitterJob>(
            TimeEventEmitterJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            (routineOptions ?? new RoutineOptions()).EmitterCron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OutboxDispatcherJob>(
            OutboxDispatcherJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OutboxDispatcherJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<OutboxCleanupJob>(
            OutboxCleanupJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OutboxCleanupJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // E07/F01/US02/T03 (NF-88, NF-20): the CRM profiles and contacts of the bookers of every booking changed in the last two hours, and the
        // guests with no profile yet (200 a run, until none is left), every 15 minutes. It guarantees what the event handler only hastens, and
        // does nothing, with one line in the log, unless Features:CrmMarketing is on and Crm:ContactIndexKey is configured (read at every run).
        // Always registered: nothing to remove when the module is closed.
        recurringJobManager.AddOrUpdate<CrmProfileSyncJob>(
            CrmProfileSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CrmProfileSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // ADR-025-R3, R6: the popup alerts held back by the quiet hours of a user, or snoozed by the user, are pushed to its open sessions
        // when they fall due, every minute, whatever the flags say (a user with the app open all night would never see them otherwise).
        recurringJobManager.AddOrUpdate<PopupAlertDueJob>(
            PopupAlertDueJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            PopupAlertDueJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
