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
    /// <summary>What a supplier that never saved a rule gets (<see cref="SupplierAgendaDefaults"/>).</summary>
    public static SupplierPlanningRules Default { get; } = new(
        SupplierAgendaDefaults.BufferMinutes,
        SupplierAgendaDefaults.MaxJobsPerDay,
        SupplierAgendaDefaults.MinNoticeHours,
        SupplierAgendaDefaults.HorizonDays,
        SupplierAgendaDefaults.SlotStepMinutes,
        SupplierAgendaDefaults.ParallelJobs);
}

/// <summary>
/// One band of the weekly working hours: minutes after midnight on the wall clock of Europe/Rome, <c>EndMinute</c> up to 1440
/// (midnight). The planner turns it into UTC for each date (<see cref="Utilities.RomeCalendar.ToUtc(DateOnly, int)"/>).
/// </summary>
public readonly record struct SupplierWeeklyBand(DayOfWeek Weekday, int StartMinute, int EndMinute);

/// <summary>Days from <paramref name="From"/> to <paramref name="To"/>, both included (Europe/Rome calendar days).</summary>
public readonly record struct SupplierDateRange(DateOnly From, DateOnly To);

/// <summary>A stretch of time in UTC: <c>[StartUtc, EndUtc)</c>.</summary>
public readonly record struct SupplierInterval(DateTime StartUtc, DateTime EndUtc);

/// <summary>What takes a supplier's time, for <see cref="SupplierOccupancy"/>.</summary>
public enum SupplierOccupancyKind
{
    /// <summary>A service request that counts (Richiesto, PresoInCarico or InCorso), with its hours when it has them.</summary>
    Request,

    /// <summary>A booking from the public showcase waiting for the customer's e-mail check (SP-10); it lapses at its expiry.</summary>
    Hold,

    /// <summary>Hours the supplier blocked by hand.</summary>
    Block,

    /// <summary>An engagement of the supplier's own calendar (the iCal feed, SP-05).</summary>
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
        DateTime? expiresAtUtc)
    {
        Kind = kind;
        StartUtc = startUtc;
        EndUtc = endUtc;
        Day = day;
        ExpiresAtUtc = expiresAtUtc;
    }

    public SupplierOccupancyKind Kind { get; }

    /// <summary>First instant (UTC); <c>null</c> for a request that only has a day.</summary>
    public DateTime? StartUtc { get; }

    /// <summary>Instant it ends (UTC), after <see cref="StartUtc"/>; <c>null</c> for a request that only has a day.</summary>
    public DateTime? EndUtc { get; }

    /// <summary>The Europe/Rome day of a request without hours; <c>null</c> for the others.</summary>
    public DateOnly? Day { get; }

    /// <summary>When a <see cref="SupplierOccupancyKind.Hold"/> lapses; <c>null</c> for the others.</summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>True when it has hours the slots must avoid.</summary>
    public bool HasInterval => StartUtc is not null && EndUtc is not null;

    /// <summary>True for a request and a hold: they count for <c>MaxJobsPerDay</c>.</summary>
    public bool CountsTowardsDailyMax => Kind is SupplierOccupancyKind.Request or SupplierOccupancyKind.Hold;

    /// <summary>A request with hours (accepted or not yet: it holds the place as soon as it exists).</summary>
    public static SupplierOccupancy TimedRequest(DateTime startUtc, DateTime endUtc) =>
        new(SupplierOccupancyKind.Request, startUtc, RequireAfter(startUtc, endUtc), null, null);

    /// <summary>A request that only has a day (a host's request before the time is agreed): no hour is taken, the day's count goes up.</summary>
    public static SupplierOccupancy DatedRequest(DateOnly day) =>
        new(SupplierOccupancyKind.Request, null, null, day, null);

    /// <summary>A booking of the public showcase waiting for the e-mail check, until <paramref name="expiresAtUtc"/>.</summary>
    public static SupplierOccupancy Hold(DateTime startUtc, DateTime endUtc, DateTime expiresAtUtc) =>
        new(SupplierOccupancyKind.Hold, startUtc, RequireAfter(startUtc, endUtc), null, expiresAtUtc);

    /// <summary>Hours the supplier blocked.</summary>
    public static SupplierOccupancy Block(DateTime startUtc, DateTime endUtc) =>
        new(SupplierOccupancyKind.Block, startUtc, RequireAfter(startUtc, endUtc), null, null);

    /// <summary>An engagement of the supplier's own calendar.</summary>
    public static SupplierOccupancy External(DateTime startUtc, DateTime endUtc) =>
        new(SupplierOccupancyKind.External, startUtc, RequireAfter(startUtc, endUtc), null, null);

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
public sealed record SupplierPlanningInput(
    DateTime NowUtc,
    SupplierPlanningRules Rules,
    IReadOnlyList<SupplierWeeklyBand> WeeklyHours,
    IReadOnlyList<SupplierDateRange> TimeOff,
    IReadOnlySet<DateOnly> ClosedDays,
    IReadOnlyList<SupplierInterval> ExtraOpenings,
    IReadOnlyList<SupplierOccupancy> Occupancies);

/// <summary>What the slots are for: how long the service lasts, and what it asks of the supplier's own rules.</summary>
/// <param name="DurationMinutes">How long the work lasts (a slot is that long).</param>
/// <param name="MinNoticeHours">The service's own notice (<c>SupplierServiceListing.MinNoticeHours</c>); <c>null</c> is the supplier's.</param>
/// <param name="WeekdaysMask">The days the service is offered on (<see cref="SupplierServiceWeekdays"/>); <c>null</c> is every day.</param>
public sealed record SupplierSlotQuery(int DurationMinutes, int? MinNoticeHours = null, int? WeekdaysMask = null);

/// <summary>A free slot: the work can start at <see cref="StartUtc"/> and ends at <see cref="EndUtc"/> (UTC).</summary>
public readonly record struct SupplierSlot(DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Why a day has no slot at all (SP-03). Checked in this order, so a day closed for several reasons shows the first.
/// </summary>
public enum SupplierDayClosure
{
    /// <summary>The day is before today (Europe/Rome).</summary>
    Past,

    /// <summary>The supplier is off (time off).</summary>
    TimeOff,

    /// <summary>The day is closed by hand or by the supplier's calendar feed (<c>SupplierAvailability</c> false).</summary>
    DayClosed,

    /// <summary>The service is not offered on that weekday (<c>SupplierServiceListing.WeekdaysMask</c>).</summary>
    ServiceNotOffered,

    /// <summary>No working band that weekday and no extra opening that day.</summary>
    NoHours,

    /// <summary><c>MaxJobsPerDay</c> is reached: the day is full.</summary>
    MaxJobsReached,

    /// <summary>The whole day is inside the notice: it ends before the earliest start (now plus the notice).</summary>
    WithinNotice,

    /// <summary>The day is further away than the horizon.</summary>
    BeyondHorizon,
}

/// <summary>
/// The plan of one Europe/Rome day: why it is closed (<see cref="Closure"/>), or its free slots in start order. An open day
/// can have no free slot (everything is taken): it is then full of work, not closed.
/// </summary>
public sealed record SupplierDayPlan(DateOnly Day, SupplierDayClosure? Closure, IReadOnlyList<SupplierSlot> Slots)
{
    /// <summary>True when the day has no slot for a structural reason (<see cref="Closure"/> says which).</summary>
    public bool IsClosed => Closure is not null;
}
