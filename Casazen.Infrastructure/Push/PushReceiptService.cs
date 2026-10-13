using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Push;

/// <summary>Counts of one <see cref="PushReceiptService.CheckReceiptsAsync"/> run, for the log and the tests.</summary>
public sealed record PushReceiptRunResult(
    int Checked,
    int Delivered,
    int Failed,
    int DevicesRemoved,
    int Unavailable,
    int Purged);

/// <summary>
/// Reads the Expo receipts of the accepted pushes (MO-04, A6-29). An ok ticket only means Expo queued the message; FCM
/// and APNs errors, <c>DeviceNotRegistered</c> first of all, arrive in the receipt, available about 15 minutes after the
/// send and kept by Expo for a day. A <c>DeviceNotRegistered</c> receipt removes the device registration of that token;
/// every error is logged with its code and the delivery id, never the token. Old delivery rows are purged here too.
/// Called by the recurring job <c>push-receipts</c>.
/// </summary>
public sealed class PushReceiptService(
    AppDbContext db,
    IExpoPushClient expoClient,
    TimeProvider timeProvider,
    ILogger<PushReceiptService> logger)
{
    /// <summary>Expo advises reading a receipt about 15 minutes after the send.</summary>
    public static readonly TimeSpan ReceiptDelay = TimeSpan.FromMinutes(15);

    /// <summary>Expo keeps receipts for 24 hours: a ticket older than that without a receipt will never get one.</summary>
    public static readonly TimeSpan ReceiptLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Days a delivery row is kept. It must outlive the Hangfire retries of the delivery job (a few hours), which rely on
    /// the rows to never send an event twice.
    /// </summary>
    public const int RetentionDays = 7;

    /// <summary>Tickets read per run, at most; the rest waits for the next run.</summary>
    public const int MaxTicketsPerRun = 10 * ExpoPushOptions.MaxReceiptIdsPerRequest;

    /// <summary>Rows purged per run, at most.</summary>
    public const int MaxPurgedPerRun = 5000;

    public async Task<PushReceiptRunResult> CheckReceiptsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var readyBefore = now - ReceiptDelay;
        var expiredBefore = now - ReceiptLifetime;

        var accepted = await db.PushDeliveries
            .Where(d => d.Status == PushDeliveryStatus.Accepted && d.TicketId != null && d.SentAt <= readyBefore)
            .OrderBy(d => d.SentAt)
            .ThenBy(d => d.Id)
            .Take(MaxTicketsPerRun)
            .ToListAsync(cancellationToken);

        int delivered = 0, failed = 0, removed = 0, unavailable = 0;
        foreach (var batch in accepted.Chunk(ExpoPushOptions.MaxReceiptIdsPerRequest))
        {
            var result = await expoClient.GetReceiptsAsync(batch.Select(d => d.TicketId!).ToList(), cancellationToken);
            if (!result.Succeeded)
            {
                // Logged by the client; the tickets are read again at the next run (Expo keeps them for a day).
                logger.LogWarning(
                    "Push receipts not read for {Count} tickets ({Error}): retried at the next run",
                    batch.Length,
                    result.Error);
                break;
            }

            var unregisteredTokens = new List<string>();
            var completedAt = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var delivery in batch)
            {
                if (!result.Receipts.TryGetValue(delivery.TicketId!, out var receipt))
                {
                    // Not ready yet, or no longer kept by Expo.
                    if (delivery.SentAt < expiredBefore)
                    {
                        delivery.Status = PushDeliveryStatus.ReceiptUnavailable;
                        delivery.CompletedAt = completedAt;
                        unavailable++;
                    }

                    continue;
                }

                delivery.CompletedAt = completedAt;
                if (receipt.Ok)
                {
                    delivery.Status = PushDeliveryStatus.Delivered;
                    delivered++;
                    continue;
                }

                delivery.Status = PushDeliveryStatus.Failed;
                delivery.Error = receipt.Error;
                failed++;
                logger.LogWarning(
                    "Push {Type} not delivered (delivery {DeliveryId}, device registration {DeviceRegistrationId}): {Error}",
                    delivery.Type,
                    delivery.Id,
                    delivery.DeviceRegistrationId,
                    receipt.Error);
                if (ExpoPushErrors.IsDeviceNotRegistered(receipt.Error))
                    unregisteredTokens.Add(delivery.PushToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (unregisteredTokens.Count > 0)
                removed += await PushDeviceCleanup.RemoveUnregisteredAsync(db, unregisteredTokens, logger, cancellationToken);
        }

        var purged = await PurgeAsync(now, cancellationToken);
        var summary = new PushReceiptRunResult(accepted.Count, delivered, failed, removed, unavailable, purged);
        logger.LogInformation(
            "Push receipts: {Checked} tickets checked, {Delivered} delivered, {Failed} failed, {DevicesRemoved} devices removed, {Unavailable} without receipt, {Purged} old rows purged",
            summary.Checked,
            summary.Delivered,
            summary.Failed,
            summary.DevicesRemoved,
            summary.Unavailable,
            summary.Purged);
        return summary;
    }

    /// <summary>Deletes the delivery rows older than <see cref="RetentionDays"/> days.</summary>
    private async Task<int> PurgeAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-RetentionDays);
        var old = await db.PushDeliveries
            .Where(d => d.CreatedAt < cutoff)
            .OrderBy(d => d.CreatedAt)
            .Take(MaxPurgedPerRun)
            .ToListAsync(cancellationToken);
        if (old.Count == 0)
            return 0;

        db.PushDeliveries.RemoveRange(old);
        await db.SaveChangesAsync(cancellationToken);
        return old.Count;
    }
}
