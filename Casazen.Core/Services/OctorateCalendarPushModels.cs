using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>One range of dates of one property, both ends included (E08/F05/US02/T01): what a change of the calendar sends to the channel manager.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="From">First calendar day of the range, included.</param>
/// <param name="To">Last calendar day of the range, included.</param>
public sealed record CalendarWindow(Guid PropertyId, DateOnly From, DateOnly To)
{
    /// <summary>How many days the window covers.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>
/// What CasaZen writes for the nights of an interval of the calendar of a room (<c>values</c> of <c>POST /rest/v1/calendar/bulk</c>, E08/F05/US02/T01,
/// NF-36). A member left <c>null</c> is not sent: Octorate keeps what it holds for it.
/// </summary>
/// <param name="Availability">The units available (<c>availability</c>); only when the nights are closed with it (<c>Octorate:Calendar:CloseNightsWith</c>).</param>
/// <param name="Price">The price of a night, two decimals, in the currency of the accommodation.</param>
/// <param name="MinStay">The fewest nights of a stay that starts on the night (<c>minstay</c>).</param>
/// <param name="MaxStay">The most nights of a stay that starts on the night (<c>maxstay</c>).</param>
/// <param name="StopSells">The night is stopped for sale (<c>stopSells</c>).</param>
/// <param name="CloseToArrival">No arrival on the night (<c>closeToArrival</c>).</param>
/// <param name="CloseToDeparture">No departure on the day (<c>closeToDeparture</c>).</param>
/// <param name="CutOffDays">The fewest days ahead a stay may be booked (<c>cutOffDays</c>): the minimum advance of the rule, 0 when none.</param>
public sealed record OctorateCalendarValues(
    int? Availability = null,
    decimal? Price = null,
    int? MinStay = null,
    int? MaxStay = null,
    bool? StopSells = null,
    bool? CloseToArrival = null,
    bool? CloseToDeparture = null,
    int? CutOffDays = null)
{
    /// <summary>No member is set: there is nothing to write.</summary>
    public bool IsEmpty =>
        Availability is null
        && Price is null
        && MinStay is null
        && MaxStay is null
        && StopSells is null
        && CloseToArrival is null
        && CloseToDeparture is null
        && CutOffDays is null;
}

/// <summary>One element of the array of the bulk call: one room, one interval of nights (both days included) and the values written to every night of it.</summary>
/// <param name="RoomId">The room (unit) of Octorate, <see cref="OctorateBookingTarget.ProductId"/>.</param>
/// <param name="From">First night, included.</param>
/// <param name="To">Last night, included.</param>
/// <param name="Values">What is written to each night.</param>
public sealed record OctorateCalendarUpdate(string RoomId, DateOnly From, DateOnly To, OctorateCalendarValues Values)
{
    /// <summary>How many nights the interval covers.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>
/// What the channel manager answered to a bulk call it did not refuse: the ids of the processes it runs for the call (<c>process</c>). <b>None means
/// nothing was written</b>: the sandbox of 2 November answers 200 <c>success: true</c> with an empty <c>process</c> and a warning when the room has no license
/// (E08/F05/US02/T01), and that answer is never an acceptance.
/// </summary>
/// <param name="ProcessIds">The ids of the processes, in the order Octorate gave them; empty when it wrote nothing.</param>
public sealed record OctorateCalendarUpdateResult(IReadOnlyList<string> ProcessIds);

/// <summary>One date of the process of a bulk call and what became of it on the channels (<c>sent[]</c> of <c>GET /rest/v1/calendar/process/{id}</c>).</summary>
/// <param name="Date">The date, when the answer gives one.</param>
/// <param name="Status">The status as Octorate spells it (<c>NOT_PROCESSED</c>, <c>ERROR</c>, ...); <c>null</c> when it gives none.</param>
public sealed record OctorateCalendarProcessDate(DateOnly? Date, string? Status);

/// <summary>
/// The state of the process Octorate runs for a bulk call (<c>GET /rest/v1/calendar/process/{id}</c>, documented only in Postman, decision D7): its status
/// (<c>NEW</c>, ...) and the dates it has sent, each with its status. What an answer means is decided by <see cref="CalendarPushRules.VerdictOf"/>.
/// </summary>
/// <param name="Id">The id of the process.</param>
/// <param name="Status">The status of the process as Octorate spells it; <c>null</c> when it gives none.</param>
/// <param name="Dates">The dates the process has sent.</param>
public sealed record OctorateCalendarProcess(string Id, string? Status, IReadOnlyList<OctorateCalendarProcessDate> Dates);

/// <summary>
/// What <see cref="ICalendarChangeRecorder.RecordAsync"/> added to the unit of work: the process and its chunks, not saved. The caller saves them with its
/// own changes, runs <see cref="NeedsRun"/> processes once the save is committed, and detaches them when it gives the change up.
/// </summary>
/// <param name="Process">The process of the change.</param>
/// <param name="Chunks">Its chunks, in the order of the windows and of the dates.</param>
public sealed record CalendarChangeRecord(CalendarChangeProcess Process, IReadOnlyList<CalendarChangeChunk> Chunks)
{
    /// <summary>The id of the process.</summary>
    public Guid ProcessId => Process.Id;

    /// <summary>The process is <c>Queued</c>: there is something to send, so a run is enqueued once the save is committed.</summary>
    public bool NeedsRun => Process.State == CalendarChangeState.Queued;
}
