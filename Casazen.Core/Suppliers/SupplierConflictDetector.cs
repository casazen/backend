using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The stable codes (and severities) of the conflicts of <see cref="SupplierConflictDetector"/>, as the console API sends them
/// (<c>GET api/supplier/calendar/conflicts</c>). <b>Only codes</b>: the texts are the frontend's (i18n). Never rename a code, the
/// frontend branches on it.
/// </summary>
public static class SupplierConflictCodes
{
    public const string TimeOff = "time_off";

    public const string Overlap = "overlap";

    public const string External = "external";

    public const string Block = "block";

    /// <summary>The day is closed by hand, or the supplier does not work that weekday (no hours and no extra opening). Warning.</summary>
    public const string DayOff = "day_off";

    /// <summary>The day has hours but the job is not inside them. Warning.</summary>
    public const string OutsideHours = "outside_hours";

    /// <summary><c>MaxJobsPerDay</c> would be exceeded: the day already holds as many other jobs as the supplier takes at most. Warning.</summary>
    public const string DayFull = "day_full";

    /// <summary>The job is closer than the pause (<c>BufferMinutes</c>) to another thing that takes the supplier's time. Warning.</summary>
    public const string Buffer = "buffer";

    /// <summary>
    /// Every code, in the order a job lists its conflicts: the four of danger first, then the ones of warning. The answer keeps this
    /// order, so a new code goes where its severity puts it.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [TimeOff, Overlap, External, Block, DayOff, OutsideHours, DayFull, Buffer];

    public const string Danger = "danger";

    public const string Warning = "warning";

    /// <summary>The severity of <paramref name="code"/>: <see cref="Danger"/> for leave, an overlap, an engagement or a block; else <see cref="Warning"/>.</summary>
    public static string SeverityOf(string code) => code is TimeOff or Overlap or External or Block ? Danger : Warning;
}

public sealed record SupplierConflictRequest(
    Guid Id,
    ServiceRequestStatus Status,
    DateTime StartUtc,
    DateTime EndUtc,
    int? ServiceMinNoticeHours = null,
    int? ServiceWeekdaysMask = null)
{
    public bool IsCommitted => ServiceRequestStateMachine.IsTakenJob(Status);

    public bool CanProposeTime => Status == ServiceRequestStatus.Richiesto;

    public int DurationMinutes => Math.Max(1, (int)Math.Ceiling((EndUtc - StartUtc).TotalMinutes));
}

public sealed record SupplierConflict(string Code, Guid? OtherRequestId = null, Guid? WindowId = null)
{
    /// <summary><see cref="SupplierConflictCodes.Danger"/> or <see cref="SupplierConflictCodes.Warning"/>: a function of the code.</summary>
    public string Severity => SupplierConflictCodes.SeverityOf(Code);
}

public sealed record SupplierRequestConflicts(
    Guid RequestId,
    ServiceRequestStatus Status,
    DateTime StartUtc,
    DateTime EndUtc,
    IReadOnlyList<SupplierConflict> Conflicts,
    SupplierSlot? SuggestedSlot)
{
    public DateOnly Date => RomeCalendar.DateInRome(StartUtc);
}

/// <summary>
/// What is wrong with the supplier's jobs to come (SP-03b, <c>GET api/supplier/calendar/conflicts</c>). A <b>pure function</b> like
/// the planner: it reads no clock, no database, no configuration. It is fed by the same <see cref="SupplierPlanningInput"/> as
/// <see cref="SupplierSlotPlanner"/> and asks the same <see cref="SupplierSlotPlanner.PlanningContext"/> the same questions (closed
/// day, time off, hours of the day, jobs of the day, what is in the way with the pause), so the two cannot drift: a job has a
/// conflict exactly when the planner would not offer its time (see below for what is left out on purpose).
/// </summary>
/// <remarks>
/// <para><b>Which jobs.</b> A request with hours that starts at <c>NowUtc</c> or later, in status <c>Richiesto</c>, <c>PresoInCarico</c>
/// or <c>InCorso</c> (<see cref="ServiceRequestStateMachine.IsOpenJob"/>). A job that has started, is over, was rejected or cancelled
/// is not a conflict to solve.</para>
/// <para><b>What counts against a job.</b> Everything that takes the supplier's time except the job itself: other requests, bookings
/// waiting for their e-mail check that have not lapsed, blocks and engagements of the calendar feed. <b>With one exception: what
/// still waits for an answer (a request <c>Richiesto</c>, a hold, <see cref="SupplierOccupancy.IsWaiting"/>) never counts against a
/// job the supplier has already taken</b> (it is the waiting one that has to give way); between two jobs that wait, each counts
/// against the other.</para>
/// <para><b>The codes</b> (<see cref="SupplierConflictCodes"/>), at most one per code per job. The first group is about the day, the
/// first that applies wins: <c>time_off</c> (the supplier is on leave), then <c>external</c> (the day was closed by the calendar feed)
/// or <c>day_off</c> (closed by hand, or a weekday with no hours and no extra opening), then <c>outside_hours</c> (the day has hours
/// and the job is not inside one of them; extra openings are hours). The second group is about the time: <c>overlap</c>,
/// <c>external</c> and <c>block</c> (the job overlaps another job, an engagement of the calendar, a block: half-open
/// <c>[start, end)</c>, <see cref="SupplierInterval.Overlaps"/>), and <c>buffer</c> (not overlapping, but closer than the pause to
/// something). The third: <c>day_full</c> (the day already holds, without this job, as many jobs as <c>MaxJobsPerDay</c>, bookings
/// waiting included). With capacity above one (<c>ParallelJobs</c>, not offered by the console) the time conflicts are reported only
/// when the planner would find no room.</para>
/// <para><b>What is not a conflict.</b> The rules of <i>offering</i> a new slot: the notice, the horizon, the weekdays of a service,
/// the grid of the slots (a job at 10:07, or as long as its host typed, is a job like any other). They decide what a customer can
/// book, not whether a job already in the agenda is a problem (SP-D28: the supplier decides about a time outside the published
/// hours; the detector only says why it is outside).</para>
/// <para><b>The suggestion</b> is the first slot of <see cref="SupplierSlotPlanner.PlanRange"/> from the day of the job on, for
/// <see cref="SupplierAgendaLimits.ConflictSuggestionDays"/> more days (the job is left out of the input, like
/// <c>PlanAsync(exceptRequestId)</c>), as long as the job, with the notice and the weekdays of its service; only for a request
/// <c>Richiesto</c>, the only one a time can be proposed on. The input must reach that far past the job: the caller builds it for
/// the range and for those days after it.</para>
/// </remarks>
public static class SupplierConflictDetector
{
    public static IReadOnlyList<SupplierRequestConflicts> Detect(
        SupplierPlanningInput input,
        IReadOnlyList<SupplierConflictRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(requests);

        var nowUtc = UtcDateTime.Normalize(input.NowUtc);
        var results = new List<SupplierRequestConflicts>();
        foreach (var request in requests
                     .Where(request => IsChecked(request, nowUtc))
                     .OrderBy(request => request.StartUtc)
                     .ThenBy(request => request.Id))
        {
            var conflicts = ConflictsOf(input, request, nowUtc);
            if (conflicts.Count == 0)
                continue;

            results.Add(new SupplierRequestConflicts(
                request.Id,
                request.Status,
                request.StartUtc,
                request.EndUtc,
                conflicts,
                request.CanProposeTime ? FirstFreeSlot(input, request) : null));
        }

        return results;
    }

    public static IReadOnlyList<SupplierConflict> ConflictsOf(SupplierPlanningInput input, SupplierConflictRequest request)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        return ConflictsOf(input, request, UtcDateTime.Normalize(input.NowUtc));
    }

    /// <summary>
    /// True for a job the detector looks at: <see cref="ServiceRequestStateMachine.IsOpenJob"/>, with hours of a positive length,
    /// that start at <paramref name="nowUtc"/> or later. Public so that a caller that bounds its work (the service cuts the list at
    /// <see cref="SupplierAgendaLimits.MaxConflictRequests"/>) bounds it on the jobs that count, by this same rule.
    /// </summary>
    public static bool IsChecked(SupplierConflictRequest request, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ServiceRequestStateMachine.IsOpenJob(request.Status)
               && request.EndUtc > request.StartUtc
               && request.StartUtc >= nowUtc;
    }

    private static IReadOnlyList<SupplierConflict> ConflictsOf(SupplierPlanningInput input, SupplierConflictRequest request, DateTime nowUtc)
    {
        var interval = new SupplierInterval(request.StartUtc, request.EndUtc);
        var day = RomeCalendar.DateInRome(request.StartUtc);

        var against = input.Occupancies.Where(occupancy => occupancy.IsLive(nowUtc) && CountsAgainst(occupancy, request)).ToList();
        var context = new SupplierSlotPlanner.PlanningContext(
            input with { Occupancies = against },
            new SupplierSlotQuery(request.DurationMinutes, MinNoticeHours: 0));

        var found = new Dictionary<string, SupplierConflict>(StringComparer.Ordinal);

        void Add(string code, SupplierOccupancy? other = null) => found.TryAdd(code, ConflictWith(code, other));

        if (!SupplierSlotPlanner.HasRoom(request.StartUtc, request.EndUtc, context.Widened, context.Capacity))
        {
            var inTheWay = against
                .Where(occupancy => occupancy.Widened(context.Buffer) is { } stretch && interval.Overlaps(stretch))
                .OrderBy(occupancy => occupancy.Committed ? 0 : 1)
                .ThenBy(occupancy => occupancy.StartUtc)
                .ThenBy(occupancy => occupancy.SourceId);
            foreach (var other in inTheWay)
            {
                var overlapsTheJob = other.Interval is { } own && interval.Overlaps(own);
                Add(overlapsTheJob ? OverlapCodeOf(other.Kind) : SupplierConflictCodes.Buffer, other);
            }
        }

        if (context.IsOnTimeOff(day))
            Add(SupplierConflictCodes.TimeOff);
        else if (context.IsClosedDay(day))
            Add(input.CalendarClosedDays?.Contains(day) == true ? SupplierConflictCodes.External : SupplierConflictCodes.DayOff);
        else if (!context.HasHours(day))
            Add(SupplierConflictCodes.DayOff);
        else if (!SupplierSlotPlanner.BandsOf(day, context).Any(band => band.StartUtc <= request.StartUtc && request.EndUtc <= band.EndUtc))
            Add(SupplierConflictCodes.OutsideHours);

        if (context.IsDayFull(day))
            Add(SupplierConflictCodes.DayFull);

        return SupplierConflictCodes.All.Where(found.ContainsKey).Select(code => found[code]).ToList();
    }

    private static bool CountsAgainst(SupplierOccupancy other, SupplierConflictRequest request) =>
        !IsTheRequest(other, request.Id) && !(request.IsCommitted && other.IsWaiting);

    private static bool IsTheRequest(SupplierOccupancy occupancy, Guid requestId) =>
        occupancy.Kind == SupplierOccupancyKind.Request && occupancy.SourceId == requestId;

    private static string OverlapCodeOf(SupplierOccupancyKind kind) => kind switch
    {
        SupplierOccupancyKind.External => SupplierConflictCodes.External,
        SupplierOccupancyKind.Block => SupplierConflictCodes.Block,
        _ => SupplierConflictCodes.Overlap,
    };

    private static SupplierConflict ConflictWith(string code, SupplierOccupancy? other)
    {
        Guid? requestId = other is { Kind: SupplierOccupancyKind.Request } ? other.SourceId : null;
        Guid? windowId = other is { Kind: SupplierOccupancyKind.Block or SupplierOccupancyKind.External } ? other.SourceId : null;
        return new SupplierConflict(code, requestId, windowId);
    }

    private static SupplierSlot? FirstFreeSlot(SupplierPlanningInput input, SupplierConflictRequest request)
    {
        var withoutIt = input with { Occupancies = input.Occupancies.Where(occupancy => !IsTheRequest(occupancy, request.Id)).ToList() };
        var first = RomeCalendar.DateInRome(request.StartUtc);
        var plans = SupplierSlotPlanner.PlanRange(
            first,
            first.AddDays(SupplierAgendaLimits.ConflictSuggestionDays),
            withoutIt,
            new SupplierSlotQuery(request.DurationMinutes, request.ServiceMinNoticeHours, request.ServiceWeekdaysMask));

        foreach (var plan in plans)
        {
            if (plan.Slots.Count > 0)
                return plan.Slots[0];
        }

        return null;
    }
}
