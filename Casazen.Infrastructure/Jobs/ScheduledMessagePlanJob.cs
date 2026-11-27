using Casazen.Core.Features;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The hourly plan of the scheduled messages (E05/F07/US01/T02, NF-71): for each organization that has an active routine with a message (or a message still to settle), every stay whose check-in or check-out is
/// within <c>Messaging:ScheduledMessages:PlanHorizonDays</c> is planned again (<see cref="IScheduledMessageService.ReplanOrgAsync"/>), so a routine activated, edited or switched off is reflected within the hour and
/// a booking event that was missed is recovered.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Flag.</b> Scheduled only while <c>Features:ScheduledMessages</c> is on (the registration removes it otherwise), and a run with it off does nothing: a job writes only while the flag of its module is on.</item>
/// <item><b>When.</b> At minute 20 of every hour (UTC): the sweep of the messages runs every 15 minutes at minutes 0, 15, 30 and 45, so the two never start together.</item>
/// <item><b>Failures.</b> One organization's failure never stops the others; the first is thrown when the pass is over, so the run shows as failed. No retry by Hangfire: the next run, an hour later, looks at the same stays.</item>
/// </list>
/// </remarks>
public class ScheduledMessagePlanJob(
    IScheduledMessageService messages,
    IFeatureFlags featureFlags,
    ILogger<ScheduledMessagePlanJob> logger)
{
    public const string RecurringJobId = "scheduled-message-plan";

    /// <summary>At minute 20 of every hour (UTC).</summary>
    public const string Cron = "20 * * * *";

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.ScheduledMessages))
        {
            logger.LogDebug("Scheduled messages are off (Features:ScheduledMessages): nothing is planned");
            return;
        }

        var orgs = await messages.OrgsWithMessageRoutinesAsync(cancellationToken);
        var changed = 0;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        foreach (var orgId in orgs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                changed += await messages.ReplanOrgAsync(orgId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The scheduled messages of org {OrgId} could not be planned", orgId);
                failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }

        logger.LogInformation("Scheduled messages plan: {Orgs} organizations looked at, {Changed} rows written or changed", orgs.Count, changed);
        failure?.Throw();
    }
}
