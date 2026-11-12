namespace Casazen.Core.Services;

/// <summary>
/// Sends the chunks of a calendar change to the channel manager and records what it answered (E08/F05/US02/T01, NF-36, NF-78): the work of the job that
/// runs a process, once right after the change is committed and again, if something is left, when the process is due.
/// </summary>
/// <remarks>
/// <para><b>Only a connected property, only while the connector is on.</b> Nothing leaves while <c>Features:OctorateConnector</c> is off (the chunks wait),
/// and a chunk whose property is not mirrored any more (the connection was lost, the mapping is to decide again) is <c>Skipped</c>, never sent.</para>
/// <para><b>What an answer means.</b> A process id is <c>Accepted</c>; success <b>without</b> a process id writes nothing and is <c>Failed</c> with
/// <c>octorate_room_not_licensed</c>, never accepted. A rate limit, a 5xx or a timeout keeps the chunk <c>Pending</c> with the next attempt a back-off
/// later (1, 2, 4, 8 minutes) and fails it with <c>octorate_unavailable</c> after the fifth attempt; a refusal of the access is
/// <c>octorate_not_authorized</c>; another refusal <c>octorate_request_rejected</c>. A quota that is nearly used postpones the chunk with
/// <c>octorate_quota_low</c> on the process and the next attempt five minutes later, never a failure.</para>
/// <para><b>The state of the process</b> is derived from its chunks (<see cref="CalendarPushRules.Derive"/>) and never says «published»: sent is
/// «accepted by the channel manager».</para>
/// </remarks>
public interface ICalendarPushService
{
    /// <summary>
    /// Sends the <c>Pending</c> chunks of the process, one call for each (under the lock of the process: a second run that finds it taken leaves), and
    /// derives the state of the process from its chunks. Does nothing for a process that is not <c>Queued</c> or <c>Sending</c>, or that does not exist. A
    /// failure that is not the channel manager's (a bug, the database) is not swallowed: it goes up and the chunks keep what was saved.
    /// </summary>
    Task RunAsync(Guid processId, CancellationToken cancellationToken = default);
}

/// <summary>
/// What follows a calendar change after it was recorded (E08/F05/US02/T02, NF-36): the processes that are due, the confirmation of the chunks the channel
/// manager accepted, and the retry the host asks for.
/// </summary>
public interface ICalendarChangeRunner
{
    /// <summary>
    /// Runs the processes that are due (<c>Queued</c>, <c>Sending</c>, <c>Sent</c> or <c>PartiallySent</c> with <c>NextAttemptAt</c> reached): the oldest first, a bounded batch,
    /// one org at a time, so the quota of one org never starves another. A process with chunks to send is sent (<see cref="ICalendarPushService.RunAsync"/>), then the
    /// state of the chunks the channel manager accepted is asked (<c>GET /rest/v1/calendar/process/{id}</c>): <c>NEW</c> and <c>NOT_PROCESSED</c> leave the chunk
    /// accepted, <c>ERROR</c> fails it with <c>octorate_channel_error</c>, and it is confirmed only when the process and every date show a status of
    /// <c>Octorate:Calendar:ProcessSuccessStatuses</c> (empty until the sandbox shows them: nothing is confirmed; an unknown, absent or interim status confirms nothing); a
    /// process that is wholly confirmed is asked once more after ten minutes, so an error that shows up later is not left out of sight; an endpoint that is not available (404, an
    /// answer that cannot be read) leaves it accepted and claims nothing (decision D7), and a quota that is used up waits five minutes. Does nothing while the connector is off.
    /// Returns how many processes it ran.
    /// </summary>
    Task<int> RunDueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks again for a process the host sees failed or partly sent: its failed chunks go back to <c>Pending</c>, the process to <c>Queued</c>, and a run is
    /// enqueued. Returns the new state.
    /// </summary>
    /// <exception cref="Exceptions.DomainConflictException"><c>calendar_change_not_retryable</c> (409): the process is not <c>Failed</c> or <c>PartiallySent</c>.</exception>
    /// <exception cref="Exceptions.NotFoundException">There is no such process for the caller.</exception>
    Task<Entities.Enums.CalendarChangeState> RetryAsync(Guid processId, CancellationToken cancellationToken = default);
}
