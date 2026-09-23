using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

public class GdprDataRetentionJob(
    AppDbContext context,
    IGdprService gdprService,
    ILogger<GdprDataRetentionJob> logger)
{
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        // System job: no tenant filter, every org's guests. Each guest is anonymized within its own org (TN-1).
        var expiredGuests = await context.Guests
            .Where(g => !g.IsDeleted && g.DataRetentionUntil < DateTime.UtcNow)
            .Select(g => new { g.Id, g.OrgId })
            .ToListAsync();

        logger.LogInformation("GDPR retention job: {Count} guest(s) past retention period", expiredGuests.Count);

        foreach (var guest in expiredGuests)
        {
            try
            {
                await gdprService.AnonymizeGuestDataAsync(guest.OrgId, guest.Id);
            }
            catch (NotFoundException)
            {
                logger.LogInformation("GDPR retention job: guest {GuestId} no longer exists", guest.Id);
            }
        }
    }
}
