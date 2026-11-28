using Casazen.Infrastructure.Identity;
using Hangfire;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The hourly retention of the self-hosted identity verification (E73/F01/US02/T03, NF-195, decision D9): the evidence of the reviewers past its time or of a decided booking,
/// the abandoned sessions and their kept pictures, the checks of the document and the verification logs past their retention. All the decisions are in
/// <see cref="IdentityRetentionService"/>; this only runs it. Always scheduled (<c>RecurringJobsRegistration</c>): the service reads <c>Features:CheckInIdentitySelfHosted</c> and
/// <c>Features:CheckInIdentityReviewEvidence</c> again at every run, and with both off it deletes only the evidence past its time (a flag switched off never leaves a biometric picture
/// with no expiry). Its arguments carry no data: the run takes everything from the database.
/// </summary>
/// <remarks>Two runs never overlap (<c>identity-evidence-purge</c>): one that finds the lock taken waits up to five minutes and then fails, and Hangfire repeats it. Idempotent.</remarks>
[DisableConcurrentExecution(RecurringJobId, 300)]
public class IdentityEvidencePurgeJob(IdentityRetentionService retention, TimeProvider clock)
{
    public const string RecurringJobId = "identity-evidence-purge";

    public const string Cron = "40 * * * *";

    [AutomaticRetry(Attempts = 3)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) =>
        await retention.PurgeAsync(clock.GetUtcNow().UtcDateTime, cancellationToken);
}
