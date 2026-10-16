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
    /// <summary>The supplier is on leave that day (a time off covers it). Danger.</summary>
    public const string TimeOff = "time_off";

    /// <summary>
    /// The job overlaps another job (or a booking waiting for its e-mail check). The same <c>[start, end)</c> rule as the 409
    /// <c>supplier_commitment_conflict</c> of taking a job (<see cref="SupplierInterval.Overlaps"/>). Danger.
    /// </summary>
    public const string Overlap = "overlap";

    /// <summary>The job overlaps an engagement of the supplier's own calendar (the iCal feed), or falls on a day it closed. Danger.</summary>
    public const string External = "external";

    /// <summary>The job overlaps hours the supplier blocked by hand. Danger.</summary>
    public const string Block = "block";

    /// <summary>The day is closed by hand, or the supplier does not work that weekday (no hours and no extra opening). Warning.</summary>
    public const string DayOff = "day_off";

    /// <summary>The day has hours but the job is not inside them. Warning.</summary>
    public const string OutsideHours = "outside_hours";

    /// <summary><c>MaxJobsPerDay</c> would be exceeded: the day already holds as many other jobs as the supplier takes at most. Warning.</summary>
    public const string DayFull = "day_full";

    /// <summary>The job is closer than the pause (<c>BufferMinutes</c>) to another thing that takes the supplier's time. Warning.</summary>
    public const string Buffer = "buffer";

    /// <summary>Every code, in the order a request lists them inside the same severity.</summary>
    public static IReadOnlyList<string> All { get; } = [TimeOff, Overlap, External, Block, DayOff, OutsideHours, DayFull, Buffer];

    /// <summary>The severity of a conflict that makes the time impossible to keep as it is.</summary>
    public const string Danger = "danger";

    /// <summary>The severity of a conflict the supplier may decide to live with.</summary>
    public const string Warning = "warning";

    /// <summary>The severity of <paramref name="code"/>: <see cref="Danger"/> for leave, an overlap, an engagement or a block; else <see cref="Warning"/>.</summary>
    public static string SeverityOf(string code) => code is TimeOff or Overlap or External or Block ? Danger : Warning;
}

/// <summary>
/// A job of the supplier to check (SP-03b): a request with hours. <see cref="Status"/> decides what counts against it and whether a
/// time can be suggested for it.
/// </summary>
/// <param name="ServiceMinNoticeHours">The notice of the catalog service of the request (the supplier's own when <c>null</c>): only for the suggestion.</param>
/// <param name="ServiceWeekdaysMask">The weekdays the catalog service is offered on (<see cref="SupplierServiceWeekdays"/>), <c>null</c> every day: only for the suggestion.</param>
public sealed record SupplierConflictRequest(
    Guid Id,
    ServiceRequestStatus Status,
    DateTime StartUtc,
    DateTime EndUtc,
    int? ServiceMinNoticeHours = null,
    int? ServiceWeekdaysMask = null)
{
    /// <summary>True for a job the supplier has taken (<c>PresoInCarico</c>, <c>InCorso</c>).</summary>
    public bool IsCommitted => Status is ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso;

    /// <summary>True while the supplier can still propose another time (<c>propose-time</c> works only on a request <c>Richiesto</c>).</summary>
    public bool CanProposeTime => Status == ServiceRequestStatus.Richiesto;

    /// <summary>How long the work lasts, in whole minutes (at least 1): what a suggested slot must be as long as.</summary>
    public int DurationMinutes => Math.Max(1, (int)Math.Ceiling((EndUtc - StartUtc).TotalMinutes));
}

/// <summary>
/// One conflict of a job: a stable <see cref="Code"/> (<see cref="SupplierConflictCodes"/>), its <see cref="Severity"/> and, when
/// the code is about another thing, which one: <see cref="OtherRequestId"/> for another request, <see cref="WindowId"/> for a block
/// or an engagement of the calendar feed (the console finds its title in the calendar). A booking waiting for its e-mail check has
/// neither.
/// </summary>
public sealed record SupplierConflict(string Code, string Severity, Guid? OtherRequestId = null, Guid? WindowId = null);

/// <summary>A job that has at least one conflict, with its conflicts (most severe first) and, when there is one, a time to propose instead.</summary>
/// <param name="Date">The Europe/Rome day of the job.</param>
/// <param name="SuggestedSlot">
/// The first free slot as long as the job from its day on (the one the planner offers once the job is out of its own way), only for a
/// request still <c>Richiesto</c> (the supplier can propose it); <c>null</c> for a job already taken and when no slot is free soon.
/// </param>
public sealed record SupplierRequestConflicts(
    Guid RequestId,
    ServiceRequestStatus Status,
    DateOnly Date,
    DateTime StartUtc,
    DateTime EndUtc,
    IReadOnlyList<SupplierConflict> Conflicts,
    SupplierSlot? SuggestedSlot);

/// <summary>
/// What is wrong with the supplier's jobs to come (SP-03b, <c>GET api/supplier/calendar/conflicts</c>). A <b>pure function</b> like
/// the planner: it reads no clock, no database, no configuration. It is fed by the same <see cref="SupplierPlanningInput"/> as
/// <see cref="SupplierSlotPlanner"/> and asks the same <see cref="SupplierSlotPlanner.PlanningContext"/> the same questions (closed
/// day, time off, hours of the day, jobs of the day, what is in the way with the pause), so the two cannot drift: a job has a
/// conflict exactly when the planner would not offer its time (see below for what is left out on purpose).
/// </summary>
/// <remarks>
/// <para><b>Which jobs.</b> A request with hours that starts at <c>NowUtc</c> or later, in status <c>Richiesto</c>, <c>PresoInCarico</c>
/// or <c>InCorso</c>. A job that has started, is over, was rejected or cancelled is not a conflict to solve.</para>
/// <para><b>What counts against a job.</b> Everything that takes the supplier's time except the job itself: other requests, bookings
/// waiting for their e-mail check that have not lapsed, blocks and engagements of the calendar feed. <b>With one exception: what
/// still waits for an answer (a request <c>Richiesto</c>, a hold) never counts against a job the supplier has already taken</b> (it
/// is the waiting one that has to give way); between two jobs that wait, each counts against the other.</para>
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
/// <para><b>The suggestion</b> is the first slot of <see cref="SupplierSlotPlanner.PlanRange"/> from the day of the job on (the job
/// is left out of the input, like <c>PlanAsync(exceptRequestId)</c>), as long as the job, with the notice and the weekdays of its
/// service; only for a request <c>Richiesto</c>, the only one a time can be proposed on.</para>
/// </remarks>
public static class SupplierConflictDetector
{
    /// <summary>
    /// The jobs of <paramref name="requests"/> that have a conflict, by start, each with its conflicts (most severe first, then in
    /// the order of <see cref="SupplierConflictCodes.All"/>). A job with no conflict is not in the answer.
    /// </summary>
    /// <param name="input">What the planner knows, with <see cref="SupplierOccupancy.SourceId"/> and <see cref="SupplierOccupancy.Committed"/> set on the requests.</param>
    /// <param name="requests">The jobs to look at (the detector leaves out the ones that are not to come or not a job).</param>
    /// <param name="suggestionDays">How many days after the day of a job the first free slot is looked for.</param>
    public static IReadOnlyList<SupplierRequestConflicts> Detect(
        SupplierPlanningInput input,
        IReadOnlyList<SupplierConflictRequest> requests,
        int suggestionDays = SupplierAgendaLimits.ConflictSuggestionDays)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegative(suggestionDays);

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
                RomeCalendar.DateInRome(request.StartUtc),
                request.StartUtc,
                request.EndUtc,
                conflicts,
                request.CanProposeTime ? FirstFreeSlot(input, request, suggestionDays) : null));
        }

        return results;
    }

    /// <summary>
    /// The conflicts of one job, most severe first. Public so a test (and the planner-equivalence property) can ask about one job
    /// without the suggestion.
    /// </summary>
    public static IReadOnlyList<SupplierConflict> ConflictsOf(SupplierPlanningInput input, SupplierConflictRequest request)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        return ConflictsOf(input, request, UtcDateTime.Normalize(input.NowUtc));
    }

    private static bool IsChecked(SupplierConflictRequest request, DateTime nowUtc) =>
        request.Status is ServiceRequestStatus.Richiesto or ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso
        && request.EndUtc > request.StartUtc
        && request.StartUtc >= nowUtc;

    private static IReadOnlyList<SupplierConflict> ConflictsOf(SupplierPlanningInput input, SupplierConflictRequest request, DateTime nowUtc)
    {
        var interval = new SupplierInterval(request.StartUtc, request.EndUtc);
        var day = RomeCalendar.DateInRome(request.StartUtc);

        // What is in the way of the job: the planner's own reading of it (the same pause, the same lapsed holds), without the job.
        var against = input.Occupancies.Where(occupancy => occupancy.IsLive(nowUtc) && CountsAgainst(occupancy, request)).ToList();
        var context = new SupplierSlotPlanner.PlanningContext(
            input with { Occupancies = against },
            new SupplierSlotQuery(request.DurationMinutes, MinNoticeHours: 0));

        var found = new Dictionary<string, SupplierConflict>(StringComparer.Ordinal);

        void Add(string code, SupplierOccupancy? other = null) => found.TryAdd(code, ConflictWith(code, other));

        // The time: asked of the planner (is there room for these hours with the pause?), then told apart by what is in the way.
        var buffer = TimeSpan.FromMinutes(Math.Max(0, input.Rules.BufferMinutes));
        var capacity = Math.Max(1, input.Rules.ParallelJobs);
        if (!SupplierSlotPlanner.HasRoom(request.StartUtc, request.EndUtc, context.Widened, capacity))
        {
            foreach (var other in against
                         .Where(occupancy => occupancy.HasInterval)
                         .OrderBy(occupancy => occupancy.Committed ? 0 : 1)
                         .ThenBy(occupancy => occupancy.StartUtc)
                         .ThenBy(occupancy => occupancy.SourceId))
            {
                if (interval.Overlaps(other.Interval!.Value))
                    Add(OverlapCodeOf(other.Kind), other);
                else if (interval.Overlaps(other.Widened(buffer)!.Value))
                    Add(SupplierConflictCodes.Buffer, other);
            }
        }

        // The day: the first reason of the planner's own order that applies.
        if (context.IsOnTimeOff(day))
            Add(SupplierConflictCodes.TimeOff);
        else if (context.IsClosedDay(day))
            Add(input.CalendarClosedDays?.Contains(day) == true ? SupplierConflictCodes.External : SupplierConflictCodes.DayOff);
        else if (!context.HasHours(day))
            Add(SupplierConflictCodes.DayOff);
        else if (!SupplierSlotPlanner.BandsOf(day, context).Any(band => band.StartUtc <= request.StartUtc && request.EndUtc <= band.EndUtc))
            Add(SupplierConflictCodes.OutsideHours);

        // The count: what the day holds without this job.
        if (context.JobsOn(day) >= input.Rules.MaxJobsPerDay)
            Add(SupplierConflictCodes.DayFull);

        return found.Values
            .OrderBy(conflict => conflict.Severity == SupplierConflictCodes.Danger ? 0 : 1)
            .ThenBy(conflict => IndexOf(conflict.Code))
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="other"/> is in the way of <paramref name="request"/>: everything but the request itself, and, for
    /// a job already taken, nothing that still waits for an answer.
    /// </summary>
    private static bool CountsAgainst(SupplierOccupancy other, SupplierConflictRequest request)
    {
        if (IsTheRequest(other, request.Id))
            return false;

        return !(request.IsCommitted
                 && (other.Kind == SupplierOccupancyKind.Hold || (other.Kind == SupplierOccupancyKind.Request && !other.Committed)));
    }

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
        return new SupplierConflict(code, SupplierConflictCodes.SeverityOf(code), requestId, windowId);
    }

    private static int IndexOf(string code)
    {
        for (var i = 0; i < SupplierConflictCodes.All.Count; i++)
        {
            if (SupplierConflictCodes.All[i] == code)
                return i;
        }

        return SupplierConflictCodes.All.Count;
    }

    /// <summary>
    /// The first slot the planner offers for the job moved: its day on, as long as the job, the job out of its own way (what
    /// <c>PlanAsync(exceptRequestId)</c> does with the rows of the database), with the notice and weekdays of its service.
    /// </summary>
    private static SupplierSlot? FirstFreeSlot(SupplierPlanningInput input, SupplierConflictRequest request, int suggestionDays)
    {
        var withoutIt = input with { Occupancies = input.Occupancies.Where(occupancy => !IsTheRequest(occupancy, request.Id)).ToList() };
        var first = RomeCalendar.DateInRome(request.StartUtc);
        var plans = SupplierSlotPlanner.PlanRange(
            first,
            first.AddDays(suggestionDays),
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
