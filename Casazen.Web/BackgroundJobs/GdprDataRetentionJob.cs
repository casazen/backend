using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

public class GdprDataRetentionJob(
    AppDbContext context,
    IGdprService gdprService,
    ILogger<GdprDataRetentionJob> logger)
{
    public async Task ExecuteAsync()
    {
        var expiredGuests = await context.Guests
            .Where(g => !g.IsDeleted && g.DataRetentionUntil < DateTime.UtcNow)
            .Select(g => g.Id)
            .ToListAsync();

        logger.LogInformation("GDPR retention job: {Count} guest(s) past retention period", expiredGuests.Count);

        foreach (var guestId in expiredGuests)
        {
            await gdprService.AnonymizeGuestDataAsync(guestId);
        }
    }
}
