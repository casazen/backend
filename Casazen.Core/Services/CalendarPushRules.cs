using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>What the process Octorate runs for a bulk call says about it (<see cref="CalendarPushRules.VerdictOf"/>).</summary>
public enum CalendarProcessVerdict
{
    /// <summary>Not finished, or an answer that does not say it is: the chunk stays accepted and nothing is claimed.</summary>
    Pending = 0,

    /// <summary>Every date of the process was processed.</summary>
    Confirmed = 1,

    /// <summary>The process reports an error.</summary>
    Error = 2,
}

/// <summary>
/// The rules of the push of a calendar change that need no database and no clock (E08/F05/US02/T01, T02, NF-36): how many times a chunk is tried and
/// when, the state of a process derived from its chunks, how the ids of the processes of the channel manager are kept in the column of a chunk, and what
/// the answer about a process means. Pure, so the push, the job that follows it, the read of the changes and the tests agree.
/// </summary>
public static class CalendarPushRules
{
    /// <summary>How many times a chunk is sent before a channel manager that does not answer makes it <c>Failed</c> (<c>octorate_unavailable</c>).</summary>
    public const int MaxAttempts = 5;

    /// <summary>How long a process whose chunks wait for quota is left alone before the next run (a postponement, never a failure).</summary>
    public static TimeSpan QuotaRetryAfter { get; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the state of the processes the channel manager runs is asked while a chunk is accepted and not confirmed.</summary>
    public static TimeSpan ConfirmationEvery { get; } = TimeSpan.FromMinutes(2);

    /// <summary>How long after its acceptance a chunk is followed: past it the chunk stays accepted, worded «accepted by the channel manager», and is not asked again.</summary>
    public static TimeSpan ConfirmationWindow { get; } = TimeSpan.FromMinutes(30);

    /// <summary>How long after a process was confirmed the channel manager is asked once more about it: an error that shows up after the confirmation stays visible.</summary>
    public static TimeSpan ConfirmedRecheckAfter { get; } = TimeSpan.FromMinutes(10);

    /// <summary>Marks a list of process ids that did not fit the column of the chunk: the ids that fit are kept and the list ends with it.</summary>
    public const char TruncatedMarker = '+';

    private const char IdSeparator = ',';

    /// <summary>The wait before the next attempt of a chunk that was tried <paramref name="attempts"/> times: 1, 2, 4 and then 8 minutes.</summary>
    public static TimeSpan BackoffAfter(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(4),
        _ => TimeSpan.FromMinutes(8),
    };

    /// <summary>
    /// The state of a process from the states of its chunks: <see cref="CalendarChangeState.Applied"/> when there is nothing to send, <c>Queued</c> or
    /// <c>Sending</c> while a chunk is still to send, then <c>Failed</c> (no chunk accepted), <c>PartiallySent</c> (some accepted, some failed),
    /// <c>Sent</c> (every chunk accepted, some not confirmed yet) or <c>Confirmed</c> (every chunk confirmed). It never says «published»: <c>Sent</c> is
    /// the channel manager accepting the work.
    /// </summary>
    public static CalendarChangeState Derive(IEnumerable<CalendarChunkState> chunkStates)
    {
        ArgumentNullException.ThrowIfNull(chunkStates);

        int pending = 0, accepted = 0, confirmed = 0, failed = 0, total = 0;
        foreach (var state in chunkStates)
        {
            total++;
            switch (state)
            {
                case CalendarChunkState.Pending:
                    pending++;
                    break;
                case CalendarChunkState.Accepted:
                    accepted++;
                    break;
                case CalendarChunkState.Confirmed:
                    confirmed++;
                    break;
                case CalendarChunkState.Failed:
                    failed++;
                    break;
            }
        }

        if (total == 0)
            return CalendarChangeState.Applied;

        if (pending > 0)
            return accepted + confirmed > 0 ? CalendarChangeState.Sending : CalendarChangeState.Queued;

        if (failed > 0)
            return accepted + confirmed > 0 ? CalendarChangeState.PartiallySent : CalendarChangeState.Failed;

        // Nothing is left to send and nothing failed: all the chunks were skipped, or the channel manager took the rest.
        if (accepted + confirmed == 0)
            return CalendarChangeState.Applied;

        return accepted > 0 ? CalendarChangeState.Sent : CalendarChangeState.Confirmed;
    }

    /// <summary>The process has settled: no chunk is still to send or to confirm (the moment <see cref="CalendarChangeProcess.FinishedAt"/> is set).</summary>
    public static bool IsSettled(IEnumerable<CalendarChunkState> chunkStates)
    {
        ArgumentNullException.ThrowIfNull(chunkStates);

        return !chunkStates.Any(state => state is CalendarChunkState.Pending or CalendarChunkState.Accepted);
    }

    /// <summary>
    /// The ids of the processes of the channel manager as kept in <see cref="CalendarChangeChunk.OctorateProcessId"/>: distinct, in order, separated by
    /// commas. A list that does not fit the column keeps the ids that fit and ends with <see cref="TruncatedMarker"/>, so a chunk whose ids are not all kept
    /// is never read as confirmed. <c>null</c> for no id.
    /// </summary>
    public static string? JoinProcessIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var distinct = ids
            .Select(id => id?.Trim() ?? string.Empty)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
            return null;

        var limit = CalendarChangeChunk.OctorateProcessIdMaxLength;
        var all = string.Join(IdSeparator, distinct);
        if (all.Length <= limit)
            return all;

        // The marker takes one character of the column.
        var kept = new List<string>();
        var length = 1;
        foreach (var id in distinct)
        {
            var next = id.Length + (kept.Count > 0 ? 1 : 0);
            if (length + next > limit)
                break;

            kept.Add(id);
            length += next;
        }

        return string.Join(IdSeparator, kept) + TruncatedMarker;
    }

    /// <summary>The ids kept in a chunk (<see cref="JoinProcessIds"/>) and whether they are all the channel manager gave (no <see cref="TruncatedMarker"/>).</summary>
    public static (IReadOnlyList<string> Ids, bool Complete) ParseProcessIds(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return ([], true);

        var text = stored.Trim();
        var complete = !text.EndsWith(TruncatedMarker);
        if (!complete)
            text = text[..^1];

        var ids = text.Split(IdSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (ids, complete);
    }

    /// <summary>
    /// What the process of a bulk call says (decision D7: the endpoint is documented only in Postman, with <c>NEW</c>, <c>NOT_PROCESSED</c> and <c>ERROR</c> as the only examples:
    /// no status of success is documented). <c>ERROR</c> anywhere is an error; <c>NEW</c> or <c>NOT_PROCESSED</c> anywhere, or no date at all, is not finished. It is confirmed
    /// only when the process and every one of its dates show a status that <paramref name="successStatuses"/> names (<c>Octorate:Calendar:ProcessSuccessStatuses</c>, empty until
    /// the sandbox shows them): a status that is unknown, absent or an interim one (<c>PROCESSING</c>) is never read as a success, and with no success status configured nothing is
    /// ever confirmed.
    /// </summary>
    public static CalendarProcessVerdict VerdictOf(OctorateCalendarProcess process, IReadOnlySet<string> successStatuses)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(successStatuses);

        var statuses = process.Dates.Select(date => date.Status).Append(process.Status).ToList();
        if (statuses.Any(status => Is(status, "ERROR")))
            return CalendarProcessVerdict.Error;

        if (statuses.Any(status => Is(status, "NEW") || Is(status, "NOT_PROCESSED")) || process.Dates.Count == 0)
            return CalendarProcessVerdict.Pending;

        return statuses.All(status => IsSuccess(status, successStatuses)) ? CalendarProcessVerdict.Confirmed : CalendarProcessVerdict.Pending;
    }

    private static bool Is(string? status, string expected) => string.Equals(status?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccess(string? status, IReadOnlySet<string> successStatuses) =>
        !string.IsNullOrWhiteSpace(status) && successStatuses.Contains(status.Trim());
}
