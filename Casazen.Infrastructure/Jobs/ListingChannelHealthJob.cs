using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Raises a «To do» when a channel goes silent, every day (E02/F03/US01/T07, NF-38, ADR-034-R3, R5): for every <c>Accepted</c> or <c>Published</c> row
/// whose <c>LastSyncedAt</c> is older than 24 hours, one item of the kind <see cref="ActivityType.IntegrationError"/> (the action opens the listing to
/// fix), idempotent for the property and channel (the item is about the row); it is closed when the row is fresh again, is no longer a pushed one,
/// or belongs to a property that was deleted (a deleted property keeps its rows, and no one can act on them).
/// Behind <c>Features:OctorateConnector</c> (the registration removes it while the flag is off). Nothing is sent to Octorate and nothing is personal data.
/// </summary>
/// <remarks>
/// The staleness is the one of the status (<c>syncAlert</c>): 24 hours without a confirmation. The same rule decides the alert the host reads and the item
/// the host is asked to deal with.
/// </remarks>
public class ListingChannelHealthJob(
    AppDbContext db,
    ListingTodos todos,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<ListingChannelHealthJob> logger)
{
    public const string RecurringJobId = "listing-channel-health";

    /// <summary>Every day at 06:00 (UTC).</summary>
    public const string Cron = "0 6 * * *";

    /// <summary>The seconds a run waits for the previous one.</summary>
    public const int LockTimeoutSeconds = 300;

    /// <summary>The code the item carries as its reason.</summary>
    public const string Reason = "listing_channel_silent";

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var threshold = now - ListingStatusService.SyncAlertAfter;

        // Only the channels of properties that still exist: a deleted property keeps its rows (nothing cleans them up), the SoftDelete filter hides
        // it from the «To do» service (which would throw «not found» at the first such row and stop the whole run), and nobody is left to fix them.
        var stale = await (
                from state in db.ListingChannelStates.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
                join property in db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking() on state.PropertyId equals property.Id
                where (state.Status == ChannelPublishStatus.Accepted || state.Status == ChannelPublishStatus.Published)
                      && (state.LastSyncedAt == null || state.LastSyncedAt < threshold)
                select state)
            .ToListAsync(cancellationToken);
        foreach (var row in stale)
            await todos.RaiseAsync(ActivityType.IntegrationError, row, ActivityUrgency.Medium, Reason, cancellationToken);

        // Closed when the row is fresh again (or is not a pushed one any more, or its property is gone).
        var silent = stale.Select(row => row.Id).ToHashSet();
        var type = nameof(ActivityType.IntegrationError);
        var open = await db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(a => a.Action != null && a.CompletedAt == null && a.ActivityType == type && a.EntityType == ListingTodos.EntityType)
            .Select(a => new { a.Id, EntityId = a.EntityId ?? Guid.Empty })
            .ToListAsync(cancellationToken);

        var closed = 0;
        foreach (var item in open.Where(item => !silent.Contains(item.EntityId)))
        {
            await todos.CloseItemAsync(item.Id, InternalActivity.ClosedBySystem, cancellationToken);
            closed++;
        }

        logger.LogInformation("Listing channel health: {Silent} silent channel(s) raised, {Closed} item(s) closed", stale.Count, closed);
    }
}
