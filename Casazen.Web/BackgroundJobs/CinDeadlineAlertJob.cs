using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

public class CinDeadlineAlertJob(
    AppDbContext context,
    INotificationService notificationService,
    ILogger<CinDeadlineAlertJob> logger)
{
    private const int AlertWindowDays = 7;

    public async Task ExecuteAsync()
    {
        var daysUntilDeadline = CinComplianceRules.DaysUntilDeadline();
        if (daysUntilDeadline > AlertWindowDays)
            return;

        var properties = await context.Properties
            .AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new { p.Id, p.OwnerId, p.CinCode })
            .ToListAsync();

        var nonCompliantByOwner = properties
            .Where(p => !CinComplianceRules.IsCompliant(p.CinCode))
            .GroupBy(p => p.OwnerId)
            .ToList();

        foreach (var group in nonCompliantByOwner)
        {
            var propertyIds = group.Select(p => p.Id).ToList();
            logger.LogWarning(
                "CIN deadline alert for owner {OwnerId}: {Count} non-compliant properties, {Days} days remaining",
                group.Key, propertyIds.Count, daysUntilDeadline);

            await notificationService.SendCinDeadlineAlertAsync(group.Key, propertyIds, daysUntilDeadline);
        }
    }
}
