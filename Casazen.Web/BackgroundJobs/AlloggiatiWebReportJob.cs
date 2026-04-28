using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

public class AlloggiatiWebReportJob(
    IAlloggiatiWebService alloggiatiWebService,
    AppDbContext context,
    ILogger<AlloggiatiWebReportJob> logger)
{
    public async Task ReportGuestAsync(Guid guestId, Guid bookingId)
    {
        logger.LogInformation("Processing Alloggiati Web report for booking {BookingId}", bookingId);
        await alloggiatiWebService.ReportGuestAsync(guestId, bookingId);
    }

    public async Task RetryFailedReportsAsync()
    {
        const int maxRetries = 3;
        var failedReports = await context.AlloggiatiWebReports
            .Where(r => (r.Status == AlloggiatiWebStatus.Failed || r.Status == AlloggiatiWebStatus.Pending)
                        && r.RetryCount < maxRetries)
            .ToListAsync();

        logger.LogInformation("Retrying {Count} failed/pending Alloggiati Web reports", failedReports.Count);

        foreach (var report in failedReports)
        {
            try
            {
                await alloggiatiWebService.ReportGuestAsync(report.GuestId, report.BookingId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retry failed for Alloggiati Web report {ReportId}", report.Id);
            }
        }
    }
}
