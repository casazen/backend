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
/// that leads to an item that is not there is worse than none. It also empties the words of the items that were decided or expired a while ago
/// (<see cref="ApprovalQueueRules.TextRetentionDays"/>): the text of a message to a guest, the host's edit of it and the reason of a rejection do not stay for ever.
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
    /// Process pending approval items and expire those past their expiration date, then empty the words of the old decided ones.
    /// </summary>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ProcessExpirationsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await ExpirePendingItemsAsync(now, cancellationToken);
        await EmptyTheWordsOfOldItemsAsync(now, cancellationToken);
    }

    private async Task ExpirePendingItemsAsync(DateTime now, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// The words of the items decided or expired more than <see cref="ApprovalQueueRules.TextRetentionDays"/> days ago go: the text (a message to a guest, which may name the guest), the
    /// host's edit of it and the reason of a rejection (the host's own words, which may name a guest too). What the decision was (its status, its author, its time and its action) stays.
    /// An item still pending is never touched. The text of an item that was emptied is the empty string (an empty value is stored as it is, never as a payload), so a second run finds
    /// nothing. The erasure of a guest empties the items of its conversations at once (<c>GuestDataEraser</c>); this is the end of every other one. Counts only in the log.
    /// </summary>
    private async Task EmptyTheWordsOfOldItemsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-ApprovalQueueRules.TextRetentionDays);

        // The time of the decision: when the item was approved or rejected, and, for an expired one, its time limit.
        var old = db.ApprovalQueueItems
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(i => i.Status != ApprovalStatus.Pending
                        && (i.ApprovedAt ?? i.ExpiresAt) < cutoff
                        && (i.ContentText != string.Empty || i.EditedContentText != null || i.RejectionReason != null));

        int emptied;
        if (db.Database.IsRelational())
        {
            // One statement: the words are never read (and decrypted) to be thrown away.
            emptied = await old.ExecuteUpdateAsync(
                set => set
                    .SetProperty(i => i.ContentText, string.Empty)
                    .SetProperty(i => i.EditedContentText, (string?)null)
                    .SetProperty(i => i.RejectionReason, (string?)null),
                cancellationToken);
        }
        else
        {
            // EF InMemory (unit tests) has no set-based update: the rows are changed and saved.
            var rows = await old.ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.ContentText = string.Empty;
                row.EditedContentText = null;
                row.RejectionReason = null;
            }

            await db.SaveChangesAsync(cancellationToken);
            emptied = rows.Count;
        }

        if (emptied > 0)
        {
            logger.LogInformation(
                "Emptied the words of {Count} approval queue items decided or expired more than {Days} days ago",
                emptied,
                ApprovalQueueRules.TextRetentionDays);
        }
    }
}
