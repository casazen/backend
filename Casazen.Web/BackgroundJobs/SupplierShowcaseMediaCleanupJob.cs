using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily cleanup of the images of the suppliers' showcases (SP-13b): the files that neither the draft, nor the published version, nor the
/// version published before it point to, and that are older than a day, are removed from the storage and from the inventory. A file in use is
/// never touched, whatever its age. All the decisions and the locks are in <see cref="ISupplierShowcaseMediaService"/>; this only runs it, and
/// it logs only a count. Idempotent. Always registered (a file that exists costs money whatever a flag says). Runbooks:
/// <c>docs/runbooks/suppliers.md</c> section 33b, <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class SupplierShowcaseMediaCleanupJob(ISupplierShowcaseMediaService media)
{
    public const string RecurringJobId = "supplier-showcase-media-cleanup";

    /// <summary>Every day at 04:20 UTC (after the retention jobs of 03:xx).</summary>
    public const string Cron = "20 4 * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) => await media.CleanupAsync(cancellationToken);
}
