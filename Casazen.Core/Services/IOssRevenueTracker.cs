namespace Casazen.Core.Services;

public interface IOssRevenueTracker
{
    Task<bool> IsOssThresholdReachedAsync(CancellationToken cancellationToken = default);
    Task RecordEuB2cCrossBorderRevenueAsync(decimal amountEur, CancellationToken cancellationToken = default);
}
