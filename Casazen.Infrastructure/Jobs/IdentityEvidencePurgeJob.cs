using Casazen.Infrastructure.Identity;
using Hangfire;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The hourly retention of the self-hosted identity verification (E73/F01/US02/T03, NF-195, decision D9): the evidence of the reviewers past its time or of a decided booking,
/// the abandoned sessions and their kept pictures, the checks of the document and the verification logs past their retention. All the decisions are in
/// <see cref="IdentityRetentionService"/>; this only runs it. Scheduled only while <c>Features:CheckInIdentitySelfHosted</c> or <c>Features:CheckInIdentityReviewEvidence</c> is on
/// (<c>RecurringJobsRegistration</c>), and the service reads the flags again at every run. Its arguments carry no data: the run takes everything from the database.
/// </summary>
/// <remarks>Two runs never overlap (<c>identity-evidence-purge</c>): one that finds the lock taken waits up to five minutes and then fails, and Hangfire repeats it. Idempotent.</remarks>
[DisableConcurrentExecution(RecurringJobId, 300)]
public class IdentityEvidencePurgeJob(IdentityRetentionService retention, TimeProvider clock)
{
    public const string RecurringJobId = "identity-evidence-purge";

    /// <summary>Every hour at minute 40 UTC, away from the jobs of the full hour and of minute 20.</summary>
    public const string Cron = "40 * * * *";

    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) =>
        await retention.PurgeAsync(clock.GetUtcNow().UtcDateTime, cancellationToken);
}
