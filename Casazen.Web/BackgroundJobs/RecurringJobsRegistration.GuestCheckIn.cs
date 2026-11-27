using Casazen.Core.Features;
using Casazen.Infrastructure.Jobs;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The guest check-in links, the automation of the arrivals and departures, the retry of the links that wait and the scheduled messages.
    /// </summary>
    private static void RegisterGuestCheckInJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        recurringJobManager.AddOrUpdate<GuestCheckInSendJob>(
            "guest-checkin-send",
            job => job.ExecuteAsync(),
            "0 8 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<BookingFlowAutomationJob>(
            BookingFlowAutomationJob.RecurringJobId,
            job => job.ExecuteAsync(),
            "20 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        ConfigureCheckInLinkDeliveryJob(recurringJobManager, CheckInLinkDeliveryRetryJob.IsEnabled(featureFlags));

        // E05/F07/US01/T02, T03 (NF-71): the hourly plan of the messages of the routines (minute 20) and the sweep that sends what is overdue and tries again what is blocked (every 15 minutes), only while
        // Features:ScheduledMessages is on; otherwise the schedule of an earlier deploy is removed.
        ConfigureScheduledMessageJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.ScheduledMessages));
    }

    /// <summary>
    /// The jobs of the scheduled messages (E05/F07/US01, NF-71): the hourly plan at minute 20 and the sweep every 15 minutes, scheduled only while <paramref name="enabled"/>
    /// (<c>Features:ScheduledMessages</c> on); otherwise the schedule of an earlier deploy is removed (<c>RemoveIfExists</c>) and no message is planned or sent by a job.
    /// </summary>
    private static void ConfigureScheduledMessageJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(ScheduledMessagePlanJob.RecurringJobId);
            recurringJobManager.RemoveIfExists(ScheduledMessageSweepJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<ScheduledMessagePlanJob>(
            ScheduledMessagePlanJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ScheduledMessagePlanJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        recurringJobManager.AddOrUpdate<ScheduledMessageSweepJob>(
            ScheduledMessageSweepJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            ScheduledMessageSweepJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The hourly retry of the check-in links that wait (E23/F01/US01/T01, T02): scheduled only while <paramref name="enabled"/> (the connector and at least one of the flags
    /// <c>PortalChatCheckInLink</c> and <c>GuestContactInfo</c> on); otherwise the schedule of an earlier deploy is removed, and the daily <c>guest-checkin-send</c> alone records
    /// where each link stands.
    /// </summary>
    private static void ConfigureCheckInLinkDeliveryJob(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(CheckInLinkDeliveryRetryJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<CheckInLinkDeliveryRetryJob>(
            CheckInLinkDeliveryRetryJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CheckInLinkDeliveryRetryJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
