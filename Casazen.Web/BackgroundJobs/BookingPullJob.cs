using Casazen.Core.Services;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Background job for pulling new bookings from OTA platforms
/// Runs more frequently than full sync (every 15 minutes by default)
/// </summary>
public class BookingPullJob
{
    private readonly IOtaManager _otaManager;
    private readonly ILogger<BookingPullJob> _logger;

    public BookingPullJob(IOtaManager otaManager, ILogger<BookingPullJob> logger)
    {
        _otaManager = otaManager;
        _logger = logger;
    }

    /// <summary>
    /// Pulls new bookings for a specific property from all integrated platforms
    /// </summary>
    /// <param name="propertyId">Property ID to pull bookings for</param>
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
