using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Background job for synchronizing property data across all OTA platforms
/// Runs on a recurring schedule (hourly by default)
/// </summary>
public class OtaSyncJob
{
    private readonly IOtaManager _otaManager;
    private readonly ILogger<OtaSyncJob> _logger;

    public OtaSyncJob(IOtaManager otaManager, ILogger<OtaSyncJob> logger)
    {
        _otaManager = otaManager;
        _logger = logger;
    }

    /// <summary>
    /// Synchronizes all platforms for a specific property
    /// </summary>
    /// <param name="propertyId">Property ID to sync</param>
    [DisableConcurrentExecution("OtaSyncJob.ExecuteAsync:{0}", JobLockTimeouts.DefaultSeconds)] // one sync per property at a time
    public async Task ExecuteAsync(Guid propertyId)
    {
        try
        {
            _logger.LogInformation("Starting OTA sync for property {PropertyId}", propertyId);

            var success = await _otaManager.SyncAllAsync(propertyId);

            if (success)
            {
                _logger.LogInformation("OTA sync completed successfully for property {PropertyId}", propertyId);
            }
            else
            {
                _logger.LogWarning("OTA sync completed with errors for property {PropertyId}", propertyId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OTA sync failed for property {PropertyId}", propertyId);
            throw; // Re-throw to let Hangfire handle retry logic
        }
    }
}
