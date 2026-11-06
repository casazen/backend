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
/// Looks for the changes made on Octorate outside CasaZen, every hour (E02/F03/US01/T07, NF-27, ADR-027-R1, R2, R4): for each property with a channel
/// that was pushed (<c>Accepted</c> or <c>Published</c>) and has a baseline, it asks <see cref="IOutsideChangeService.DetectAsync"/> to read the
/// groups of the content again and compare them with the baseline. A difference flags the channel and raises a «To do»; nothing is ever written to
/// Octorate. Behind <c>Features:OctorateConnector</c> (the registration removes it while the flag is off).
/// </summary>
public class ListingOutsideChangeJob(
    AppDbContext db,
    IOutsideChangeService outsideChanges,
    IHostEnvironment hostEnvironment,
    IFeatureFlags featureFlags,
    ILogger<ListingOutsideChangeJob> logger)
{
    public const string RecurringJobId = "listing-outside-change";

    /// <summary>Every hour (UTC).</summary>
    public const string Cron = "0 * * * *";

    /// <summary>The seconds a run waits for the previous one.</summary>
    public const int LockTimeoutSeconds = 300;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        var pushed = await db.ListingChannelStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => (s.Status == ChannelPublishStatus.Accepted || s.Status == ChannelPublishStatus.Published) && s.ContentHashesJson != null)
            .Select(s => s.PropertyId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var wanted = pushed.ToHashSet();
        var mapped = await OctorateBookingRuns.ListMappedPropertiesAsync(db, OctorateEnvironments.Of(hostEnvironment), cancellationToken);
        var targets = mapped.Where(m => wanted.Contains(m.PropertyId)).Select(m => (m.OrgId, m.PropertyId)).ToList();

        var (done, failed) = await ListingJobRuns.RunAsync(
            targets,
            (orgId, propertyId) => outsideChanges.DetectAsync(orgId, propertyId, cancellationToken),
            logger,
            RecurringJobId,
            cancellationToken);
        logger.LogInformation("Listing outside-change check: {Properties} properties read, {Failed} failed", done, failed);
    }
}
