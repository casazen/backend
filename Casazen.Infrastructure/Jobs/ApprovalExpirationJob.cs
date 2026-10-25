using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Daily job to expire approval queue items older than 7 days (NF-06, AC-NF06-5).
/// Moves items from Pending to Expired status.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
public class ApprovalExpirationJob(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<ApprovalExpirationJob> logger)
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
