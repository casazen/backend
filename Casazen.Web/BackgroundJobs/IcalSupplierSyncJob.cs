using Casazen.Infrastructure.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// iCal sync of the supplier calendars (runbook <c>docs/runbooks/ical.md</c>). The download never runs inside a
/// request (FD-16, SU-15): the recurring <c>ical-supplier-sync</c> job syncs every feed, <see cref="SyncSupplierAsync"/>
/// is queued when the supplier saves the URL or asks "sync now".
/// </summary>
public class IcalSupplierSyncJob
{
    private readonly CalendarSyncService _calendarSyncService;
    private readonly ILogger<IcalSupplierSyncJob> _logger;

    public IcalSupplierSyncJob(CalendarSyncService calendarSyncService, ILogger<IcalSupplierSyncJob> logger)
    {
        _calendarSyncService = calendarSyncService;
        _logger = logger;
    }

    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting scheduled iCal sync for all suppliers");
        await _calendarSyncService.SyncAllIcalFeedsAsync(cancellationToken);
        _logger.LogInformation("Scheduled iCal sync completed");
    }

    /// <summary>
    /// Sync of one supplier, queued by <c>PUT /api/supplier/calendar/ical</c> (first sync of the URL) and
    /// <c>POST /api/supplier/calendar/sync</c> ("sync now"). One run per supplier at a time; against the recurring job
    /// the supplier's advisory lock serializes the writes (<see cref="CalendarSyncService.SyncIcalFeedAsync"/>).
    /// </summary>
    [DisableConcurrentExecution("IcalSupplierSyncJob.SyncSupplierAsync:{0}", JobLockTimeouts.FrequentSeconds)]
    public async Task SyncSupplierAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        await _calendarSyncService.SyncIcalFeedAsync(orgId, cancellationToken);
    }
}
