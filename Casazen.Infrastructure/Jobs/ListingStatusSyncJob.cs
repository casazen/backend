using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Follows the publications of the listings every 15 minutes (E02/F03/US01/T07, ADR-034, NF-27, NF-32): for each property with a channel waiting for
/// its publication (<c>Sending</c> or <c>Accepted</c>) or a photo waiting in the queue of the channel manager, it asks
/// <see cref="IListingStatusService.SyncPropertyAsync"/> to read the process and the queue and to move the rows and the photo tasks. Behind
/// <c>Features:OctorateConnector</c> (the registration removes it while the flag is off).
/// </summary>
/// <remarks>
/// A system read over every org: which properties are read is the single definition of the connector (a confirmed mapping with its room, of an org whose
/// connection is connected). Each property is read for its own org under <see cref="IOctorateOrgScope.Use"/> (the service opens it); one that fails
/// leaves the others alone, and an org the channel manager refuses stops until the next run. The lock waits as long as the 15 minutes of the schedule.
/// </remarks>
public class ListingStatusSyncJob(
    AppDbContext db,
    IListingStatusService status,
    IHostEnvironment hostEnvironment,
    IFeatureFlags featureFlags,
    ILogger<ListingStatusSyncJob> logger)
{
    public const string RecurringJobId = "listing-status-sync";

    /// <summary>Every 15 minutes (UTC).</summary>
    public const string Cron = "*/15 * * * *";

    /// <summary>The seconds a run waits for the previous one.</summary>
    public const int LockTimeoutSeconds = 60;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        var rowTargets = await db.ListingChannelStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => s.Status == ChannelPublishStatus.Sending || s.Status == ChannelPublishStatus.Accepted)
            .Select(s => new { s.OrgId, s.PropertyId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var photoTargets = await db.PhotoUploadTasks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(t => t.Status == PhotoUploadStatus.Uploading || t.Status == PhotoUploadStatus.RemovePending)
            .Select(t => new { t.OrgId, t.PropertyId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var wanted = rowTargets.Concat(photoTargets).Select(target => target.PropertyId).ToHashSet();
        var mapped = await OctorateBookingRuns.ListMappedPropertiesAsync(db, OctorateEnvironments.Of(hostEnvironment), cancellationToken);
        var targets = mapped.Where(m => wanted.Contains(m.PropertyId)).Select(m => (m.OrgId, m.PropertyId)).ToList();

        var (done, failed) = await ListingJobRuns.RunAsync(
            targets,
            (orgId, propertyId) => status.SyncPropertyAsync(orgId, propertyId, cancellationToken),
            logger,
            RecurringJobId,
            cancellationToken);
        logger.LogInformation("Listing status sync: {Properties} properties read, {Failed} failed", done, failed);
    }
}
