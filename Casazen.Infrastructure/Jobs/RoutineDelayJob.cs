using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Hangfire job for resuming routine executions after Wait actions (NF-26, AC-NF26-10, ADR-022-R10).
/// Scheduled by the engine at the <c>ResumeAt</c> of the execution (<c>IBackgroundJobClient.Schedule</c>, E01/F04/US01/T01); it carries the id of the
/// execution only: the payload and the next action are the ones the execution saved when it started to wait.
/// </summary>
/// <remarks>
/// One run for an execution at a time (<see cref="LockTimeoutSeconds"/>): a second job for the same wait, a retry or a manual run waits for the first,
/// and then finds the execution no longer waiting and does nothing, so the actions after a wait run once.
/// </remarks>
public class RoutineDelayJob(
    IRoutineService routineService,
    ILogger<RoutineDelayJob> logger)
{
    public const int MaxAttempts = 3;

    /// <summary>The seconds a run waits for the one of the same execution before it.</summary>
    public const int LockTimeoutSeconds = 1800;

    /// <summary>
    /// Resume a routine execution that was waiting.
    /// Called by Hangfire at the scheduled resume time.
    /// </summary>
    /// <param name="executionId">Routine execution ID to resume.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution("RoutineDelayJob.ResumeRoutineAsync:{0}", LockTimeoutSeconds)]
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
