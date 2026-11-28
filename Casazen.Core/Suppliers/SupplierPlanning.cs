namespace Casazen.Core.Suppliers;

/// <summary>
/// The rules the slot planner applies (SP-03): the settings of a supplier, or <see cref="Default"/> for one that never saved
/// a rule. Every value is checked where it is written (<see cref="SupplierAgendaRules.NormalizeRules"/> and the database
/// checks); the planner refuses a rule that would make it loop (a step that is not positive).
/// </summary>
/// <param name="BufferMinutes">Minutes kept free before and after every job, block or engagement.</param>
/// <param name="MaxJobsPerDay">Jobs a Europe/Rome day takes at most.</param>
/// <param name="MinNoticeHours">Shortest notice, in hours, between now and the start of a slot (a service can ask for its own).</param>
/// <param name="HorizonDays">How many days ahead of today a day can still be booked.</param>
/// <param name="SlotStepMinutes">Distance between two slots that start in the same working band.</param>
/// <param name="ParallelJobs">Jobs the supplier can do at the same time (decision D10: 1).</param>
public sealed record SupplierPlanningRules(
    int BufferMinutes,
    int MaxJobsPerDay,
    int MinNoticeHours,
    int HorizonDays,
    int SlotStepMinutes,
    int ParallelJobs)
{
    public static SupplierPlanningRules Default { get; } = new(
        SupplierAgendaDefaults.BufferMinutes,
        SupplierAgendaDefaults.MaxJobsPerDay,
        SupplierAgendaDefaults.MinNoticeHours,
        SupplierAgendaDefaults.HorizonDays,
        SupplierAgendaDefaults.SlotStepMinutes,
        SupplierAgendaDefaults.ParallelJobs);
}

public readonly record struct SupplierWeeklyBand(DayOfWeek Weekday, int StartMinute, int EndMinute);

public readonly record struct SupplierDateRange(DateOnly From, DateOnly To);

public readonly record struct SupplierInterval(DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>
    /// True when the two stretches share time. Half-open, like the stretches: one that ends at the instant the other starts does
    /// not overlap it. <b>The one definition of "overlap" between two stretches of a supplier's agenda</b>: what is in the way of a
    /// slot (<see cref="SupplierSlotPlanner"/>), the check made when a job is taken or a request is created (409
    /// <c>supplier_commitment_conflict</c>) and the <c>overlap</c> conflict of the console (<see cref="SupplierConflictDetector"/>)
    /// all call it. What each one compares is its own rule: the 409 compares the jobs the supplier took, the conflict also counts
    /// what still waits for an answer.
    /// </summary>
    public bool Overlaps(SupplierInterval other) => StartUtc < other.EndUtc && other.StartUtc < EndUtc;
}

public enum SupplierOccupancyKind
{
    Request,

    Hold,

    Block,

    External,
}

/// <summary>
/// <b>The input door of the planner for everything that takes the supplier's time</b>: a list of these, built by whoever
/// knows the thing (the agenda service for blocks and calendar events, the request flow for requests with hours (SP-04),
/// the showcase booking for holds (SP-10)), so the planner never changes when a new source arrives.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>With hours</b> (<see cref="StartUtc"/>/<see cref="EndUtc"/>): the slots may not overlap it, with the buffer of the
/// supplier before and after (and, with <c>ParallelJobs</c> above 1, only while it leaves no room).</item>
/// <item><b>Without hours</b> (<see cref="DatedRequest"/>): a request that only has a day, like the ones of a host before the
/// time is agreed. It occupies no hour, but counts for the day's maximum.</item>
/// <item><b>Counts for the daily maximum</b> (<see cref="CountsTowardsDailyMax"/>): requests and holds, on the Europe/Rome day
/// of their start (or of their date). A hold is a request waiting for its e-mail check: counting it is what keeps two
/// customers from taking the last place of a day at the same time. Blocks and engagements never count.</item>
/// <item><b>Expiry</b>: a hold past <see cref="ExpiresAtUtc"/> is ignored altogether (the planner is given the instant).</item>
/// </list>
/// </remarks>
public sealed record SupplierOccupancy
{
    private SupplierOccupancy(
        SupplierOccupancyKind kind,
        DateTime? startUtc,
        DateTime? endUtc,
        DateOnly? day,
        DateTime? expiresAtUtc,
        Guid? sourceId,
        bool committed)
    {
        Kind = kind;
        StartUtc = startUtc;
        EndUtc = endUtc;
        Day = day;
        ExpiresAtUtc = expiresAtUtc;
        SourceId = sourceId;
        Committed = committed;
    }

    public SupplierOccupancyKind Kind { get; }

    /// <summary>
    /// What is behind it, when the caller knows: the id of the service request, or of the window (block or calendar engagement).
    /// The planner never reads it; the conflict detector (SP-03b) uses it to leave a request out of its own way and to say which
    /// request or window is in the way.
    /// </summary>
    public Guid? SourceId { get; }

    public bool Committed { get; }

    public DateTime? StartUtc { get; }

    public DateTime? EndUtc { get; }

    public DateOnly? Day { get; }

    public DateTime? ExpiresAtUtc { get; }

    public bool HasInterval => StartUtc is not null && EndUtc is not null;

    public bool CountsTowardsDailyMax => Kind is SupplierOccupancyKind.Request or SupplierOccupancyKind.Hold;

    public bool IsWaiting => Kind == SupplierOccupancyKind.Hold || (Kind == SupplierOccupancyKind.Request && !Committed);

    public SupplierInterval? Interval =>
        StartUtc is { } start && EndUtc is { } end ? new SupplierInterval(start, end) : null;

    public SupplierInterval? Widened(TimeSpan buffer) =>
        Interval is { } interval ? new SupplierInterval(interval.StartUtc - buffer, interval.EndUtc + buffer) : null;

    public bool IsLive(DateTime nowUtc) => !(Kind == SupplierOccupancyKind.Hold && ExpiresAtUtc <= nowUtc);

    public static SupplierOccupancy TimedRequest(DateTime startUtc, DateTime endUtc, Guid? requestId = null, bool committed = false) =>
        new(SupplierOccupancyKind.Request, startUtc, RequireAfter(startUtc, endUtc), null, null, requestId, committed);

    public static SupplierOccupancy DatedRequest(DateOnly day, Guid? requestId = null, bool committed = false) =>
        new(SupplierOccupancyKind.Request, null, null, day, null, requestId, committed);

    public static SupplierOccupancy Hold(DateTime startUtc, DateTime endUtc, DateTime expiresAtUtc) =>
        new(SupplierOccupancyKind.Hold, startUtc, RequireAfter(startUtc, endUtc), null, expiresAtUtc, null, false);

    public static SupplierOccupancy Block(DateTime startUtc, DateTime endUtc, Guid? windowId = null) =>
        new(SupplierOccupancyKind.Block, startUtc, RequireAfter(startUtc, endUtc), null, null, windowId, false);

    public static SupplierOccupancy External(DateTime startUtc, DateTime endUtc, Guid? windowId = null) =>
        new(SupplierOccupancyKind.External, startUtc, RequireAfter(startUtc, endUtc), null, null, windowId, false);

    private static DateTime RequireAfter(DateTime startUtc, DateTime endUtc) =>
        endUtc > startUtc
            ? endUtc
            : throw new ArgumentOutOfRangeException(nameof(endUtc), "The end of an occupied interval must be after its start.");
}

/// <summary>
/// Everything the planner knows about one supplier's agenda at an instant (SP-03). Built by
/// <see cref="Services.ISupplierAgendaService.BuildPlanningInputAsync"/> from the database; a test builds it by hand.
/// </summary>
/// <param name="NowUtc">The instant "now" is (UTC): the planner never reads the clock.</param>
/// <param name="Rules">Buffer, daily maximum, notice, horizon, step, capacity.</param>
/// <param name="WeeklyHours">The weekly bands (Europe/Rome wall clock); a weekday with none is a rest day.</param>
/// <param name="TimeOff">Closed spans of days (holidays, illness): no slot, whatever else the day has.</param>
/// <param name="ClosedDays">Days closed by hand or by the calendar feed (<c>SupplierAvailability</c> with <c>Available = false</c>).</param>
/// <param name="ExtraOpenings">Hours opened on top of the weekly hours (each inside one Europe/Rome day).</param>
/// <param name="Occupancies">What takes the supplier's time: requests, holds, blocks, engagements.</param>
/// <param name="CalendarClosedDays">
/// The days of <paramref name="ClosedDays"/> that the calendar feed closed (an all-day event of the supplier's own calendar), as
/// opposed to the ones the supplier closed by hand. The planner never reads it (every closed day is closed); the conflict detector
/// (SP-03b) does, to tell "another engagement" from "a day off". <c>null</c> is none.
/// </param>
/// <param name="Urgent">
/// The supplier's urgent settings (SP-04c): read only by a plan that asks for urgent slots (<see cref="SupplierSlotQuery.Urgent"/>),
/// which uses their window and finds no slot at all when they are off. <c>null</c> is a supplier that never saved them: off.
/// </param>
public sealed record SupplierPlanningInput(
    DateTime NowUtc,
    SupplierPlanningRules Rules,
    IReadOnlyList<SupplierWeeklyBand> WeeklyHours,
    IReadOnlyList<SupplierDateRange> TimeOff,
    IReadOnlySet<DateOnly> ClosedDays,
    IReadOnlyList<SupplierInterval> ExtraOpenings,
    IReadOnlyList<SupplierOccupancy> Occupancies,
    IReadOnlySet<DateOnly>? CalendarClosedDays = null,
    SupplierUrgentSettings? Urgent = null);

/// <summary>What the slots are for: how long the service lasts, and what it asks of the supplier's own rules.</summary>
/// <param name="DurationMinutes">How long the work lasts (a slot is that long).</param>
/// <param name="MinNoticeHours">The service's own notice (<c>SupplierServiceListing.MinNoticeHours</c>); <c>null</c> is the supplier's.</param>
/// <param name="WeekdaysMask">The days the service is offered on (<see cref="SupplierServiceWeekdays"/>); <c>null</c> is every day.</param>
/// <param name="Urgent">
/// The slots of an <b>urgent</b> request (SP-04c, decision SP-D9) instead of the normal ones: only today and tomorrow, 30 minutes of
/// notice (<see cref="SupplierAgendaLimits.UrgentNoticeMinutes"/>, never <paramref name="MinNoticeHours"/>), and the day's bands are
/// the supplier's urgent window (outside its normal hours too). Holidays, closed days, blocks, engagements, other requests, the
/// pause, the daily maximum, the weekdays of the service, the step and daylight saving time apply as ever. The caller asks only for a
/// service that offers urgent requests (<see cref="SupplierUrgentRules.Effective"/>); the supplier's own switch is the planner's
/// (<see cref="SupplierPlanningInput.Urgent"/>). <c>false</c> is the plan every caller got before.
/// </param>
public sealed record SupplierSlotQuery(int DurationMinutes, int? MinNoticeHours = null, int? WeekdaysMask = null, bool Urgent = false);

public readonly record struct SupplierSlot(DateTime StartUtc, DateTime EndUtc);

public enum SupplierDayClosure
{
    Past,

    TimeOff,

    DayClosed,

    ServiceNotOffered,

    NoHours,

    MaxJobsReached,

    WithinNotice,

    BeyondHorizon,

    UrgentNotOffered,
}

public sealed record SupplierDayPlan(DateOnly Day, SupplierDayClosure? Closure, IReadOnlyList<SupplierSlot> Slots)
{
    public bool IsClosed => Closure is not null;
}
