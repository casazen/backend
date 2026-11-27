using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The deletion of the guests' documents after the receipt (E20/F02/US01/T02, NF-121, NF-16, decision O4): the daily run over every org and the run for one org that is
/// queued right after an Alloggiati receipt is uploaded (never inside the request). All the decisions are in <see cref="IGuestDocumentPurgeService"/>; this only runs it.
/// Behind <c>Features:PurgeDocumentsAfterReceipt</c>, off by default: with the flag off a run reads nothing and deletes nothing, so the job is always registered.
/// </summary>
/// <remarks>
/// The two methods share one lock (<c>guest-document-purge</c>): the run queued by an upload and the daily run never overlap, whatever the org. A run that finds the lock
/// taken waits for it up to five minutes and then fails, and Hangfire repeats it. Idempotent: a second run finds nothing.
/// </remarks>
[DisableConcurrentExecution(RecurringJobId, 300)]
public class GuestDocumentPurgeJob(IGuestDocumentPurgeService purge)
{
    public const string RecurringJobId = "guest-document-purge";

    /// <summary>Every day at 03:50 UTC, after the retention jobs of the night.</summary>
    public const string Cron = "50 3 * * *";

    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) => await purge.PurgeAsync(null, cancellationToken);

    /// <summary>The run queued after an Alloggiati receipt of <paramref name="orgId"/> was uploaded: only the stays of that org.</summary>
    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteForOrgAsync(Guid orgId, CancellationToken cancellationToken) => await purge.PurgeAsync(orgId, cancellationToken);
}
