using Casazen.Infrastructure.Push;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Reads the Expo receipts of the pushes sent at least 15 minutes ago, removes the devices Expo reports as
/// <c>DeviceNotRegistered</c>, logs the other errors and purges the old delivery rows (MO-04, A6-29). Runbooks:
/// <c>docs/runbooks/hangfire.md</c> § 11, <c>docs/runbooks/mobile-release.md</c> § 9.7.
/// </summary>
public class PushReceiptsJob(PushReceiptService receiptService)
{
    public const string RecurringJobId = "push-receipts";

    /// <summary>Every 15 minutes: Expo advises reading a receipt about 15 minutes after the send.</summary>
    public const string Cron = "*/15 * * * *";

    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) =>
        await receiptService.CheckReceiptsAsync(cancellationToken);
}
