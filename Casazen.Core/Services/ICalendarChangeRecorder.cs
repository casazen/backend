using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Records, in the unit of work of whoever changes the calendar, the process that sends the change to the channel manager (E08/F05/US02/T01, NF-36, NF-78):
/// a <see cref="Entities.CalendarChangeProcess"/> and its <see cref="Entities.CalendarChangeChunk"/> rows, so a change and its push exist together or not
/// at all. It never calls <c>SaveChanges</c>: the caller saves them with its own changes, runs the process once the save is committed (<see cref="CalendarChangeRecord.NeedsRun"/>),
/// and detaches the rows if it gives the change up.
/// </summary>
/// <remarks>
/// <para><b>Which properties are sent.</b> Only a property that books into the channel manager (<see cref="IOctorateBookingTargets.LookupAsync"/>: a
/// connected account, a confirmed mapping, no write block) gets chunks; a property that is not connected, whose mapping is proposed or ambiguous, or
/// while <c>Features:OctorateConnector</c> is off, gets none and its change stays in CasaZen. A confirmed mapping whose room is not chosen gets a
/// <c>Failed</c> chunk with <c>octorate_room_not_chosen</c>. The process is <c>Queued</c> while a chunk is to send, <c>Applied</c> when none is.</para>
/// <para><b>Call it before staging anything else.</b> The lookup of the target may save the only room of the mapping (<see cref="IOctorateBookingTargets"/>):
/// whatever the caller staged before it would be saved with it, outside the caller's own save. A lookup that fails because the channel manager does not answer
/// is not an error of the change: the chunks are recorded <c>Pending</c> and the push looks the target up again.</para>
/// <para><b>Days.</b> The days before today of each property are never sent: a window is cut to start on its «today» (<c>DayRuleRules.TodayOf</c>) and one
/// that is over is dropped. Every window is split into chunks of at most <c>Octorate:Calendar:MaxDaysPerCall</c> days. No personal data is kept: the summary
/// is the property ids and dates of the windows, and what the caller adds.</para>
/// </remarks>
public interface ICalendarChangeRecorder
{
    /// <summary>
    /// Adds the process and its chunks to the unit of work.
    /// </summary>
    /// <param name="kind">What the change is.</param>
    /// <param name="windows">The ranges of dates of the properties the change touches (at least one, all of one org, both days included).</param>
    /// <param name="userId">The Auth0 subject of the account that asked for it; <c>null</c> for a change of the system.</param>
    /// <param name="summaryJson">What was asked, as a JSON object of fields, actions and values, never a name or a guest; <c>null</c> for none.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <param name="processId">The id of the process, when the caller needs it to be the same on every delivery (the event of a publication); a new one when <c>null</c>.</param>
    /// <exception cref="ArgumentException">No window, a window that ends before it starts, an invalid summary, or properties of more than one org.</exception>
    /// <exception cref="Exceptions.NotFoundException">A property does not exist for the caller (another org's, or deleted).</exception>
    Task<CalendarChangeRecord> RecordAsync(
        CalendarChangeKind kind,
        IReadOnlyCollection<CalendarWindow> windows,
        string? userId,
        string? summaryJson,
        CancellationToken cancellationToken = default,
        Guid? processId = null);
}
