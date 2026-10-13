using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily retention of the in-app notifications (UI-12a): deletes the ones older than
/// <see cref="Casazen.Core.Entities.InAppNotificationLimits.RetentionDays"/> days, read or not, of every user and org. A row holds
/// a user id, a kind of event and the id of a booking or service request, no text and no name, but it is still data about a
/// person, so it does not outlive the feed it serves. <b>Registered whatever <c>Features:InAppNotifications</c> says</b>: rows
/// written while the flag was on are purged after it is turned off (with the flag off there is nothing to find and the run is one
/// indexed delete). Idempotent. Runbooks: <c>docs/runbooks/in-app-notifications.md</c>, <c>docs/runbooks/hangfire.md</c> § 15.
/// </summary>
public class InAppNotificationRetentionJob(IInAppNotificationService notifications, ILogger<InAppNotificationRetentionJob> logger)
{
    public const string RecurringJobId = "in-app-notification-retention";

    /// <summary>Every day at 03:45 UTC (the GDPR retention runs at 03:00, the SEO events at 03:30).</summary>
    public const string Cron = "45 3 * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var deleted = await notifications.PurgeExpiredAsync(cancellationToken);
        logger.LogInformation("In-app notification retention job done: {Deleted} notifications deleted", deleted);
    }
}
