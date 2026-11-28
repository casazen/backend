using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public class BookingPullJob
{
    private readonly IOtaManager _otaManager;
    private readonly ILogger<BookingPullJob> _logger;

    public BookingPullJob(IOtaManager otaManager, ILogger<BookingPullJob> logger)
    {
        _otaManager = otaManager;
        _logger = logger;
    }

    [DisableConcurrentExecution("BookingPullJob.ExecuteAsync:{0}", JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(Guid propertyId)
    {
        try
        {
            _logger.LogInformation("Starting booking pull for property {PropertyId}", propertyId);

            var success = await _otaManager.PullBookingsAsync(propertyId);

            if (success)
            {
                _logger.LogInformation("Booking pull completed successfully for property {PropertyId}", propertyId);
            }
            else
            {
                _logger.LogWarning("Booking pull completed with errors for property {PropertyId}", propertyId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Booking pull failed for property {PropertyId}", propertyId);
            throw; // Re-throw to let Hangfire handle retry logic
        }
    }
}
