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
    /// First sync of a newly saved import URL, queued by <c>POST /api/properties/{id}/ical/import-url</c> so the
    /// download never runs inside the request (FD-16, A2-21). One run per property at a time.
    /// </summary>
    [DisableConcurrentExecution("PropertyICalSyncJob.SyncFeedAsync:{0}", JobLockTimeouts.FrequentSeconds)]
    public async Task SyncFeedAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        await _syncService.SyncPropertyFeedAsync(propertyId, cancellationToken);
    }
}
