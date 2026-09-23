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
}
