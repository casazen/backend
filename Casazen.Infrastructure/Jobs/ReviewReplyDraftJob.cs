using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The assistant writes the reply to one review (E05/F06/US01/T03, NF-69), queued by <c>ReviewReceivedHandler</c>: it runs <see cref="IReviewReplyDrafter.DraftAsync"/> under the tenant of the org of
/// the event, so the dispatcher of the events never waits for the model. <b>It only writes an item of the approval queue</b> (the draft waits for a person: a reply to a review is public, so
/// nothing is sent from here and no client of the channel manager or service of the replies is held by this class).
/// </summary>
/// <remarks>
/// <para>One run per review at a time (<see cref="LockTimeoutSeconds"/>); a failure that is not a decision (the database, a «To do» that cannot be raised) is tried again <see cref="Attempts"/> times
/// by Hangfire, and the run finds what the one before it wrote (a review with a draft waiting gets no second one). The budget used up, no provider and a reply that breaks a rule are decisions of
/// the drafter, not failures of the job.</para>
/// <para>Nothing runs while <c>Features:MessageAssistant</c> is off, also for a job queued before it was turned off. The job carries ids only.</para>
/// </remarks>
public class ReviewReplyDraftJob(
    IReviewReplyDrafter drafter,
    IFeatureFlags featureFlags,
    IBackgroundTenantScope tenantScope,
    ILogger<ReviewReplyDraftJob> logger)
{
    /// <summary>The seconds a run waits for the one of the same review before it.</summary>
    public const int LockTimeoutSeconds = 300;

    /// <summary>How many times Hangfire runs a failed job again.</summary>
    public const int Attempts = 2;

    [AutomaticRetry(Attempts = Attempts, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [DisableConcurrentExecution("ReviewReplyDraftJob.RunAsync:{1}", LockTimeoutSeconds)]
    public async Task RunAsync(Guid orgId, string reviewKey, CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.MessageAssistant))
        {
            logger.LogInformation("Review {ReviewKey} of org {OrgId}: the assistant is off, nothing is done", reviewKey, orgId);
            return;
        }

        if (!MessageThreadKey.TryParse(reviewKey, out var key) || !key.IsReview)
        {
            logger.LogWarning("Review {ReviewKey} of org {OrgId}: the key is not the one of a review, nothing is done", reviewKey, orgId);
            return;
        }

        // Every read of the run names the org; the tenant of the scope is a second wall.
        tenantScope.EnterOrg(orgId);
        var outcome = await drafter.DraftAsync(orgId, key, ReviewDraftOptions.Automatic, cancellationToken);
        logger.LogInformation(
            "Review {ReviewKey} of org {OrgId}: {Status}{Reason}",
            reviewKey,
            orgId,
            outcome.Status,
            outcome.Reason is null ? string.Empty : $" ({outcome.Reason})");
    }
}
