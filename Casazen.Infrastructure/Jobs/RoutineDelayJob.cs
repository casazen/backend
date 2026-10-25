using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Hangfire job for resuming routine executions after Wait actions (NF-26, AC-NF26-10, ADR-022-R10).
/// Scheduled to run when a routine's ResumeAt time is reached.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 1800)]
public class RoutineDelayJob(
    IRoutineService routineService,
    ILogger<RoutineDelayJob> logger)
{
    public const int MaxAttempts = 3;

    /// <summary>
    /// Resume a routine execution that was waiting.
    /// Called by Hangfire at the scheduled resume time.
    /// </summary>
    /// <param name="executionId">Routine execution ID to resume.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ResumeRoutineAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Resuming routine execution {ExecutionId}", executionId);

        try
        {
            await routineService.ResumeAsync(executionId, cancellationToken);

            logger.LogInformation("Routine execution {ExecutionId} resumed successfully", executionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resume routine execution {ExecutionId}", executionId);
            throw;
        }
    }
}
