using Casazen.Infrastructure.Services;

namespace Casazen.Web.BackgroundJobs;

public class IcalSupplierSyncJob
{
    private readonly CalendarSyncService _calendarSyncService;
    private readonly ILogger<IcalSupplierSyncJob> _logger;

    public IcalSupplierSyncJob(CalendarSyncService calendarSyncService, ILogger<IcalSupplierSyncJob> logger)
    {
        _calendarSyncService = calendarSyncService;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting scheduled iCal sync for all suppliers");
        await _calendarSyncService.SyncAllIcalFeedsAsync(cancellationToken);
        _logger.LogInformation("Scheduled iCal sync completed");
    }
}
