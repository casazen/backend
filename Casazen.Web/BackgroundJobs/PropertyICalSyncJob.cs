using Casazen.Infrastructure.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public class PropertyICalSyncJob
{
    private readonly PropertyICalSyncService _syncService;
    private readonly ILogger<PropertyICalSyncJob> _logger;

    public PropertyICalSyncJob(PropertyICalSyncService syncService, ILogger<PropertyICalSyncJob> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting scheduled property iCal sync");
        await _syncService.SyncAllFeedsAsync(cancellationToken);
        _logger.LogInformation("Scheduled property iCal sync completed");
    }

    /// <summary>
    /// Sync of one import feed, queued when the feed is added (<c>POST /api/properties/{id}/ical/feeds</c>) or by
    /// "sync now" (<c>POST .../ical/feeds/{feedId}/sync</c>), so the download never runs inside the request (FD-16,
    /// A2-21, PC-11). One run per feed at a time. A job queued before PC-11 carries a property id: no feed has that
    /// id, so it does nothing (the 15-minute job syncs the feed).
    /// </summary>
    [DisableConcurrentExecution("PropertyICalSyncJob.SyncFeedAsync:{0}", JobLockTimeouts.FrequentSeconds)]
    public async Task SyncFeedAsync(Guid feedId, CancellationToken cancellationToken = default)
    {
        await _syncService.SyncFeedAsync(feedId, cancellationToken);
    }
}
