using Casazen.Core.Features;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The sweep of the scheduled messages every 15 minutes (E05/F07/US01/T03, NF-71): it sends the planned messages that are overdue by more than <c>CatchUpMinutes</c> (the routine that should have fired did not, or
/// the message waited for the send window), tries the blocked and the failed ones again every <c>BlockedRetryMinutes</c>, and ends what has passed for good (a message more than a day late, a blocked one after the
/// check-out) with its «To do» (<see cref="IScheduledMessageDispatcher.SweepAsync"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Flag.</b> Scheduled only while <c>Features:ScheduledMessages</c> is on (the registration removes it otherwise), and a run with it off does nothing: no message goes out while the module is closed.</item>
/// <item><b>Once.</b> The dispatcher claims each row before it sends, so a run that overlaps the routine that fires or a «Send now» finds the row held and leaves it; the key of the row is the one of the message layer.</item>
/// <item><b>Failures.</b> One row's failure never stops the others; the first is thrown when the pass is over, so the run shows as failed. No retry by Hangfire: the next run, 15 minutes later, looks at the same rows.</item>
/// </list>
/// </remarks>
public class ScheduledMessageSweepJob(
    IScheduledMessageDispatcher dispatcher,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<ScheduledMessageSweepJob> logger)
{
    public const string RecurringJobId = "scheduled-message-sweep";

    /// <summary>Every 15 minutes (UTC).</summary>
    public const string Cron = "*/15 * * * *";

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.ScheduledMessages))
        {
            logger.LogDebug("Scheduled messages are off (Features:ScheduledMessages): the sweep does nothing");
            return;
        }

        var looked = await dispatcher.SweepAsync(timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        logger.LogInformation("Scheduled messages sweep: {Rows} rows looked at", looked);
    }
}
