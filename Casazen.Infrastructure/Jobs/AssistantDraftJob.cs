using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The assistant looks at one message of a guest (E05/F05/US02/T01, NF-67), queued by <c>AssistantMessageHandler</c>: it runs
/// <see cref="IAssistantDraftService.ProcessInboundAsync"/> under the tenant of the org of the event, so the dispatcher of the events never waits for the model.
/// </summary>
/// <remarks>
/// <para>One run per message at a time (<see cref="LockTimeoutSeconds"/>); a failure that is not a decision (the database, a «To do» that cannot be raised) is tried again
/// <see cref="Attempts"/> times by Hangfire, and the run finds what the one before it wrote (one row of the log per message, the draft of the message in the queue). The budget used up and a
/// provider that fails are decisions of the pipeline, not failures of the job.</para>
/// <para>Nothing runs while <c>Features:MessageAssistant</c> is off, also for a job queued before it was turned off. The job carries ids only.</para>
/// </remarks>
public class AssistantDraftJob(
    IAssistantDraftService drafts,
    IFeatureFlags featureFlags,
    IBackgroundTenantScope tenantScope,
    ILogger<AssistantDraftJob> logger)
{
    /// <summary>The seconds a run waits for the one of the same message before it.</summary>
    public const int LockTimeoutSeconds = 300;

    /// <summary>How many times Hangfire runs a failed job again.</summary>
    public const int Attempts = 2;

    [AutomaticRetry(Attempts = Attempts, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [DisableConcurrentExecution("AssistantDraftJob.RunAsync:{2}", LockTimeoutSeconds)]
    public async Task RunAsync(Guid orgId, string threadKey, Guid inboundMessageId, CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.MessageAssistant))
        {
            logger.LogInformation("Message {MessageId} of org {OrgId}: the assistant is off, nothing is done", inboundMessageId, orgId);
            return;
        }

        if (!MessageThreadKey.TryParse(threadKey, out var key))
        {
            logger.LogWarning("Message {MessageId} of org {OrgId}: the key of its conversation is not one, nothing is done", inboundMessageId, orgId);
            return;
        }

        // Every read of the run names the org; the tenant of the scope is a second wall.
        tenantScope.EnterOrg(orgId);
        var outcome = await drafts.ProcessInboundAsync(orgId, key, inboundMessageId, AssistantProcessOptions.Automatic, cancellationToken);
        logger.LogInformation(
            "Message {MessageId} of org {OrgId}: {Decision}{Already}",
            inboundMessageId,
            orgId,
            outcome.Decision,
            outcome.AlreadyProcessed ? " (already processed)" : string.Empty);
    }
}
