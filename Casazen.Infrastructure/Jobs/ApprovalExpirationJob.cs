using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Daily job to expire approval queue items older than 7 days (NF-06, AC-NF06-5).
/// Moves items from Pending to Expired status, and (E05/F04/US01/T02) closes the "To do" raised for each: an item nobody can decide any more leaves the queue, and a "To do"
/// that leads to an item that is not there is worse than none.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
public class ApprovalExpirationJob(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<ApprovalExpirationJob> logger,
    IInternalActivityService? activities = null)
{
    public const int MaxAttempts = 3;
    public const string RecurringJobId = "approval-expiration";
    public const string Cron = "0 4 * * *"; // Daily at 04:00 UTC

    /// <summary>
    /// Process pending approval items and expire those past their expiration date.
    /// </summary>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ProcessExpirationsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // IgnoreQueryFilters: job runs without tenant context, processes all orgs
        var expiredItems = await db.ApprovalQueueItems
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(i => i.Status == ApprovalStatus.Pending && i.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        if (expiredItems.Count == 0)
        {
            logger.LogInformation("No approval queue items to expire");
            return;
        }

        // The "To do" first, before the status: a close that fails stops the job with the items still pending, so the retry (or the next day) finds them again, and closing is
        // idempotent. The other way round, an item already expired would never be looked at again.
        if (activities is not null)
        {
            foreach (var activityId in expiredItems.Where(i => i.ActivityId is not null).Select(i => i.ActivityId!.Value))
            {
                try
                {
                    await activities.CloseAsync(activityId, InternalActivity.ClosedBySystem, cancellationToken);
                }
                catch (NotFoundException)
                {
                    logger.LogInformation("The To do {ActivityId} of an expired approval item was not there any more: nothing to close", activityId);
                }
            }
        }

        foreach (var item in expiredItems)
        {
            item.Status = ApprovalStatus.Expired;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Expired {Count} approval queue items past their expiration date",
            expiredItems.Count);
    }
}
