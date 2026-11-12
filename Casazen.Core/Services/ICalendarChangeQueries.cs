using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The read of the changes of the calendar sent to the channel manager (E08/F05/US02/T02, NF-36): the list of the processes and the detail of one, with the
/// state of each property derived from its chunks. A caller reaches only the processes whose properties it reaches (<see cref="HostScope"/>, in SQL like
/// every list of the host): a process that touches one property the caller does not reach is not shown to it, and is answered as a process that does not
/// exist. No personal data: ids, dates, states and stable codes.
/// </summary>
public interface ICalendarChangeQueries
{
    /// <summary>
    /// The processes of the org of <paramref name="scope"/> the caller reaches, newest first, at most <see cref="CalendarChangeListQuery.MaxLimit"/>,
    /// narrowed by the state when the query asks for one. A caller limited to some properties never gets a process of another property, and a process that
    /// names no property it can tell is not shown to it.
    /// </summary>
    Task<IReadOnlyList<CalendarChangeView>> ListAsync(HostScope scope, CalendarChangeListQuery query, CancellationToken cancellationToken = default);

    /// <summary>The process of the org of <paramref name="scope"/> with this id, or <c>null</c> when it is not there, is another org's or touches a property the caller does not reach.</summary>
    Task<CalendarChangeView?> GetAsync(HostScope scope, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>What a list of changes is narrowed by.</summary>
/// <param name="State">Only the processes in this state; <c>null</c> for every state.</param>
/// <param name="Limit">How many at most (1 to <see cref="MaxLimit"/>); <see cref="MaxLimit"/> when it is out of range.</param>
public sealed record CalendarChangeListQuery(CalendarChangeState? State = null, int Limit = CalendarChangeListQuery.MaxLimit)
{
    /// <summary>The most processes a list answers.</summary>
    public const int MaxLimit = 50;

    /// <summary>The limit of the list: <see cref="Limit"/>, or <see cref="MaxLimit"/> when it is not between 1 and <see cref="MaxLimit"/>.</summary>
    public int EffectiveLimit => Limit is >= 1 and <= MaxLimit ? Limit : MaxLimit;
}

/// <summary>Where one property of a change stands, from its chunks.</summary>
public enum CalendarChangePropertyState
{
    /// <summary>A chunk of the property is still to send.</summary>
    Pending = 0,

    /// <summary>The channel manager accepted the property's chunks; none is confirmed yet.</summary>
    Accepted = 1,

    /// <summary>The channel manager confirmed the chunks of the property.</summary>
    Confirmed = 2,

    /// <summary>A chunk of the property failed (<see cref="CalendarChangePropertyView.ErrorCode"/> says why).</summary>
    Failed = 3,

    /// <summary>Nothing was sent for the property: it is not on the channel manager, the change stays in CasaZen.</summary>
    Skipped = 4,
}

/// <summary>One property of a change, with its state and its dates.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="State">Where the property stands towards the channel manager.</param>
/// <param name="ErrorCode">The stable code of the first failed chunk of the property; <c>null</c> when none failed.</param>
/// <param name="ChunkCount">How many chunks the property has (0 when nothing is sent for it).</param>
/// <param name="From">The first day of its chunks; <c>null</c> when it has none.</param>
/// <param name="To">The last day of its chunks; <c>null</c> when it has none.</param>
public sealed record CalendarChangePropertyView(
    Guid PropertyId,
    CalendarChangePropertyState State,
    string? ErrorCode,
    int ChunkCount,
    DateOnly? From,
    DateOnly? To);

/// <summary>A change of the calendar as the host reads it.</summary>
/// <param name="Id">The process.</param>
/// <param name="Kind">What the change is.</param>
/// <param name="State">Where it stands: derived from its chunks, never «published».</param>
/// <param name="RequestedAt">When it was recorded (UTC).</param>
/// <param name="StartedAt">When the first chunk was sent (UTC); <c>null</c> until then.</param>
/// <param name="FinishedAt">When the last chunk was settled (UTC); <c>null</c> while one is to send or to confirm.</param>
/// <param name="PropertyCount">How many properties it touches.</param>
/// <param name="DayCount">How many days of all the properties together it touches.</param>
/// <param name="QuotaUnitsEstimated">The units of quota of the channel manager it is expected to use (an estimate).</param>
/// <param name="From">The first day it touches; <c>null</c> when the summary names none.</param>
/// <param name="To">The last day it touches; <c>null</c> when the summary names none.</param>
/// <param name="ErrorCode">Why the process is not whole: a stable code; <c>null</c> otherwise.</param>
/// <param name="NextAttemptAt">When the job tries it again (UTC); <c>null</c> when nothing is waiting.</param>
/// <param name="Properties">The properties of the change, with their states.</param>
public sealed record CalendarChangeView(
    Guid Id,
    CalendarChangeKind Kind,
    CalendarChangeState State,
    DateTime RequestedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int PropertyCount,
    int DayCount,
    int QuotaUnitsEstimated,
    DateOnly? From,
    DateOnly? To,
    string? ErrorCode,
    DateTime? NextAttemptAt,
    IReadOnlyList<CalendarChangePropertyView> Properties);
