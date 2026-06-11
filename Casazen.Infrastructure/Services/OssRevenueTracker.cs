using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public class OssRevenueTracker(AppDbContext dbContext) : IOssRevenueTracker
{
    private const decimal OssThresholdEur = 10_000m;

    public async Task<bool> IsOssThresholdReachedAsync(CancellationToken cancellationToken = default)
    {
        var metrics = await GetOrCreateMetricsAsync(cancellationToken);
        return metrics.OssThresholdReached;
    }

    public async Task RecordEuB2cCrossBorderRevenueAsync(decimal amountEur, CancellationToken cancellationToken = default)
    {
        var metrics = await GetOrCreateMetricsAsync(cancellationToken);
        metrics.EuB2cCrossBorderRevenue += amountEur;
        metrics.UpdatedAt = DateTime.UtcNow;

        if (!metrics.OssThresholdReached && metrics.EuB2cCrossBorderRevenue >= OssThresholdEur)
        {
            metrics.OssThresholdReached = true;
            metrics.OssSwitchoverAt = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<PlatformBillingMetrics> GetOrCreateMetricsAsync(CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        var metrics = await dbContext.PlatformBillingMetrics.FirstOrDefaultAsync(m => m.Id == 1, cancellationToken);
        if (metrics is null)
        {
            metrics = new PlatformBillingMetrics { Id = 1, CalendarYear = year };
            dbContext.PlatformBillingMetrics.Add(metrics);
            await dbContext.SaveChangesAsync(cancellationToken);
            return metrics;
        }

        if (metrics.CalendarYear != year)
        {
            metrics.CalendarYear = year;
            metrics.EuB2cCrossBorderRevenue = 0m;
            metrics.OssThresholdReached = false;
            metrics.OssSwitchoverAt = null;
            metrics.UpdatedAt = DateTime.UtcNow;
        }

        return metrics;
    }
}
