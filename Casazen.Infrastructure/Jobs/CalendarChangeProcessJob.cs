using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Sends the calendar changes to the channel manager in the background (E08/F05/US02/T02, NF-36, NF-78): <see cref="RunAsync"/> runs one process right after it was
/// recorded (a manual block, a change of the calendar, a publication) and <see cref="RunDueAsync"/> is the safety net that runs every minute: the processes that are
/// due (a postponement, a back-off, a retry), and the follow-up of what the channel manager accepted. Behind <c>Features:OctorateConnector</c>: the schedule is
/// removed while it is off and the services do nothing.
/// </summary>
/// <remarks>
/// <para>It lives next to the other jobs of the connector (<see cref="ListingPublishJob"/>) because the services that record a change, which are in this project,
/// enqueue the run after their save is committed and cannot see the web project.</para>
/// <para><b>No automatic retry of the run enqueued right after a change</b> (<c>Attempts = 0</c>): what could not be sent is retried by the recurring job on the
/// schedule of the process (the quota, the back-off), never by Hangfire at once. One run for each process at a time (the lock names the process); the recurring job holds
/// its own lock, and the services take the lock of the process in the database, so the two never send the same chunk twice.</para>
/// </remarks>
public class CalendarChangeProcessJob(ICalendarPushService push, ICalendarChangeRunner runner, ILogger<CalendarChangeProcessJob> logger)
{
    public const string RecurringJobId = "calendar-change-process";

    /// <summary>Every minute: a change reaches the channel manager within about a minute even when the run enqueued after it did not.</summary>
    public const string Cron = "* * * * *";

    /// <summary>The seconds a run waits for the previous run of the same job: the lock wait of the frequent jobs (`JobLockTimeouts.FrequentSeconds`).</summary>
    public const int LockTimeoutSeconds = 60;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [DisableConcurrentExecution("CalendarChangeProcessJob.RunAsync:{0}", LockTimeoutSeconds)]
    public async Task RunAsync(Guid processId, CancellationToken cancellationToken)
    {
        await push.RunAsync(processId, cancellationToken);
        logger.LogInformation("Calendar change {ProcessId} run ended", processId);
    }

    [DisableConcurrentExecution(LockTimeoutSeconds)]
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var ran = await runner.RunDueAsync(cancellationToken);
        if (ran > 0)
            logger.LogInformation("Calendar changes: {Count} due process(es) run", ran);
    }
}
