using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The free slots of a supplier (SP-03, <c>gap/05</c> §4.1). A <b>pure function</b>: it reads no clock, no database, no
/// configuration; everything it needs is in the <see cref="SupplierPlanningInput"/> and the <see cref="SupplierSlotQuery"/>,
/// so it is tested with tables of cases and fed, without changing, by every source of occupied time
/// (<see cref="SupplierOccupancy"/>).
/// </summary>
/// <remarks>
/// <para><b>A day has no slot</b> (<see cref="SupplierDayClosure"/>, checked in this order) when it is before today; the
/// supplier is off (time off); it is closed by hand or by the calendar feed; the service is not offered on that weekday;
/// there is no working band that weekday and no extra opening that day; <c>MaxJobsPerDay</c> is reached (requests and
/// holds of that Europe/Rome day count, also the ones without hours); the whole day is inside the notice; or it is beyond the
/// horizon (<c>day ≤ today + HorizonDays</c>).</para>
/// <para><b>Slots of an open day.</b> The working bands of the weekday (on the wall clock of Rome, turned into UTC for that
/// date by <see cref="RomeCalendar.ToUtc(DateOnly, int)"/>) and the extra openings of the day are joined into continuous
/// bands (bands that touch or overlap are one). In each band a slot starts at the beginning of the band and then every
/// <c>SlotStepMinutes</c> of real time, as long as <c>start + duration ≤ end of the band</c>. A slot is <b>free</b> when
/// it starts no earlier than <c>now + notice</c> and does not overlap anything that takes the supplier's time (requests
/// with hours, also those not accepted yet, holds that have not expired, blocks, engagements of the calendar feed), each
/// widened by <c>BufferMinutes</c> before and after; with <c>ParallelJobs</c> above 1 it is enough that the widened
/// stretches overlapping the slot are never as many as the supplier can do at once. A request that only has a day takes no
/// hour.</para>
/// <para><b>Daylight saving time</b> never moves a slot by hand: the bands become UTC instants first and the grid of slots
/// is walked in real elapsed minutes, so on 29 March (a 23-hour day) nothing is offered in the skipped hour and on
/// 25 October (a 25-hour day) the repeated hour offers its slots twice, once per pass.</para>
/// <para><b>Urgent slots</b> (SP-04c, <see cref="SupplierSlotQuery.Urgent"/>, decision SP-D9) are the same plan with three differences:
/// only today and tomorrow are open (later days are <see cref="SupplierDayClosure.BeyondHorizon"/>); the notice is
/// <see cref="SupplierAgendaLimits.UrgentNoticeMinutes"/> minutes, whatever the supplier or the service asks; and the one band of
/// the day is the urgent window of the supplier (<see cref="SupplierPlanningInput.Urgent"/>), also outside its working hours, in
/// place of the working bands and the extra openings. A supplier that does not accept urgent requests has no urgent slot on any day
/// (<see cref="SupplierDayClosure.UrgentNotOffered"/>). Everything else is unchanged: time off and the days closed by hand still close
/// the day, and blocks, engagements, requests, holds, the pause, <c>MaxJobsPerDay</c>, the weekdays of the service, the step and
/// the daylight saving time apply exactly as they do to a normal slot. A query without <c>Urgent</c> gives the plan it always gave.</para>
/// </remarks>
public static class SupplierSlotPlanner
{
    public const int MaxRangeDays = 366;

    public static SupplierDayPlan PlanDay(DateOnly day, SupplierPlanningInput input, SupplierSlotQuery query) =>
        PlanRange(day, day, input, query)[0];

    public static IReadOnlyList<SupplierDayPlan> PlanRange(
        DateOnly from,
        DateOnly to,
        SupplierPlanningInput input,
        SupplierSlotQuery query)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(query);
        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "The last day cannot be before the first.");
        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
            throw new ArgumentOutOfRangeException(nameof(to), $"A plan covers at most {MaxRangeDays} days.");
        if (query.DurationMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "The duration of a slot must be positive.");
        if (input.Rules.SlotStepMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), "The step between two slots must be positive.");

        var context = new PlanningContext(input, query);
        var plans = new List<SupplierDayPlan>(to.DayNumber - from.DayNumber + 1);
        for (var day = from; day <= to; day = day.AddDays(1))
            plans.Add(PlanOneDay(day, context));
        return plans;
    }

    private static SupplierDayPlan PlanOneDay(DateOnly day, PlanningContext context)
    {
        var closure = ClosureOf(day, context);
        if (closure is not null)
            return new SupplierDayPlan(day, closure, []);

        var dayStartUtc = RomeCalendar.StartOfDayUtc(day);
        var dayEndUtc = RomeCalendar.StartOfDayUtc(day.AddDays(1));
        var capacity = context.Capacity;
        var duration = TimeSpan.FromMinutes(context.Query.DurationMinutes);
        var step = TimeSpan.FromMinutes(context.Rules.SlotStepMinutes);

        var dayInterval = new SupplierInterval(dayStartUtc, dayEndUtc);
        var nearby = context.Widened
            .Where(interval => interval.Overlaps(dayInterval))
            .ToList();

        var slots = new List<SupplierSlot>();
        foreach (var band in BandsOf(day, context))
        {
            for (var start = band.StartUtc; start + duration <= band.EndUtc; start += step)
            {
                var end = start + duration;
                if (start >= context.EarliestStartUtc && HasRoom(start, end, nearby, capacity))
                    slots.Add(new SupplierSlot(start, end));
            }
        }

        return new SupplierDayPlan(day, null, slots);
    }

    private static SupplierDayClosure? ClosureOf(DateOnly day, PlanningContext context)
    {
        if (day < context.Today)
            return SupplierDayClosure.Past;

        if (context.UrgentNotOffered)
            return SupplierDayClosure.UrgentNotOffered;

        if (context.IsOnTimeOff(day))
            return SupplierDayClosure.TimeOff;

        if (context.IsClosedDay(day))
            return SupplierDayClosure.DayClosed;

        if (context.Query.WeekdaysMask is { } mask && !SupplierServiceWeekdays.IsOffered(mask, day.DayOfWeek))
            return SupplierDayClosure.ServiceNotOffered;

        if (!context.HasHours(day))
            return SupplierDayClosure.NoHours;

        if (context.IsDayFull(day))
            return SupplierDayClosure.MaxJobsReached;

        if (RomeCalendar.StartOfDayUtc(day.AddDays(1)) <= context.EarliestStartUtc)
            return SupplierDayClosure.WithinNotice;

        if (day > context.Today.AddDays(context.HorizonDays))
            return SupplierDayClosure.BeyondHorizon;

        return null;
    }

    /// <summary>
    /// The continuous bands of the day in UTC, in start order: the weekly bands of the weekday and the extra openings of the
    /// day, bands that touch or overlap joined into one. A band that the daylight saving change leaves with no length (it
    /// starts in the skipped hour and ends right after it) is dropped. Internal: the conflict detector asks it the same question
    /// ("is this job inside the hours of its day?") instead of keeping a copy of the rule.
    /// </summary>
    internal static List<SupplierInterval> BandsOf(DateOnly day, PlanningContext context)
    {
        if (context.UrgentWindow is { } window)
        {
            var from = RomeCalendar.ToUtc(day, window.FromMinute);
            var to = RomeCalendar.ToUtc(day, window.ToMinute);
            return to > from ? [new SupplierInterval(from, to)] : [];
        }

        var bands = new List<SupplierInterval>();
        foreach (var band in context.WeeklyBands(day.DayOfWeek))
        {
            var start = RomeCalendar.ToUtc(day, band.StartMinute);
            var end = RomeCalendar.ToUtc(day, band.EndMinute);
            if (end > start)
                bands.Add(new SupplierInterval(start, end));
        }

        bands.AddRange(context.ExtraOpeningsOn(day));
        bands.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));

        var joined = new List<SupplierInterval>(bands.Count);
        foreach (var band in bands)
        {
            if (joined.Count > 0 && band.StartUtc <= joined[^1].EndUtc)
            {
                var last = joined[^1];
                joined[^1] = last with { EndUtc = band.EndUtc > last.EndUtc ? band.EndUtc : last.EndUtc };
            }
            else
            {
                joined.Add(band);
            }
        }

        return joined;
    }

    internal static bool HasRoom(DateTime start, DateTime end, IReadOnlyList<SupplierInterval> widened, int capacity)
    {
        var slot = new SupplierInterval(start, end);
        var overlapping = widened.Where(interval => interval.Overlaps(slot)).ToList();
        if (overlapping.Count < capacity)
            return true;

        foreach (var instant in overlapping.Select(interval => interval.StartUtc > start ? interval.StartUtc : start).Distinct())
        {
            var concurrent = overlapping.Count(interval => interval.StartUtc <= instant && instant < interval.EndUtc);
            if (concurrent >= capacity)
                return false;
        }

        return true;
    }

    internal sealed class PlanningContext
    {
        private readonly ILookup<DayOfWeek, SupplierWeeklyBand> _weekly;
        private readonly ILookup<DateOnly, SupplierInterval> _extraByDay;
        private readonly Dictionary<DateOnly, int> _jobsByDay = [];

        public PlanningContext(SupplierPlanningInput input, SupplierSlotQuery query)
        {
            Query = query;
            Rules = input.Rules;
            TimeOff = input.TimeOff;
            ClosedDays = input.ClosedDays;
            Buffer = TimeSpan.FromMinutes(Math.Max(0, input.Rules.BufferMinutes));
            Capacity = Math.Max(1, input.Rules.ParallelJobs);

            var nowUtc = UtcDateTime.Normalize(input.NowUtc);
            Today = DateOnly.FromDateTime(RomeCalendar.TodayAt(new DateTimeOffset(nowUtc)));

            IsUrgent = query.Urgent;
            if (IsUrgent)
            {
                UrgentWindow = input.Urgent is { IsOn: true } urgent ? urgent : null;
                EarliestStartUtc = nowUtc.AddMinutes(SupplierAgendaLimits.UrgentNoticeMinutes);
                HorizonDays = Math.Min(input.Rules.HorizonDays, SupplierAgendaLimits.UrgentHorizonDays);
            }
            else
            {
                EarliestStartUtc = nowUtc.AddHours(Math.Max(0, query.MinNoticeHours ?? input.Rules.MinNoticeHours));
                HorizonDays = input.Rules.HorizonDays;
            }

            _weekly = input.WeeklyHours
                .Where(band => band.EndMinute > band.StartMinute)
                .OrderBy(band => band.StartMinute)
                .ToLookup(band => band.Weekday);
            _extraByDay = input.ExtraOpenings
                .Where(opening => opening.EndUtc > opening.StartUtc)
                .ToLookup(opening => RomeCalendar.DateInRome(opening.StartUtc));

            var widened = new List<SupplierInterval>();
            foreach (var occupancy in input.Occupancies)
            {
                if (!occupancy.IsLive(nowUtc))
                    continue;

                if (occupancy.CountsTowardsDailyMax)
                {
                    var day = occupancy.Day
                        ?? (occupancy.StartUtc is { } started ? RomeCalendar.DateInRome(started) : (DateOnly?)null);
                    if (day is { } counted)
                        _jobsByDay[counted] = _jobsByDay.GetValueOrDefault(counted) + 1;
                }

                if (occupancy.Widened(Buffer) is { } stretch)
                    widened.Add(stretch);
            }

            Widened = widened;
        }

        public SupplierSlotQuery Query { get; }

        public SupplierPlanningRules Rules { get; }

        public IReadOnlyList<SupplierDateRange> TimeOff { get; }

        public IReadOnlySet<DateOnly> ClosedDays { get; }

        public TimeSpan Buffer { get; }

        public int Capacity { get; }

        public DateOnly Today { get; }

        public DateTime EarliestStartUtc { get; }

        public bool IsUrgent { get; }

        public SupplierUrgentSettings? UrgentWindow { get; }

        public bool UrgentNotOffered => IsUrgent && UrgentWindow is null;

        public int HorizonDays { get; }

        public IReadOnlyList<SupplierInterval> Widened { get; }

        public IEnumerable<SupplierWeeklyBand> WeeklyBands(DayOfWeek weekday) => _weekly[weekday];

        public IEnumerable<SupplierInterval> ExtraOpeningsOn(DateOnly day) => _extraByDay[day];

        public bool HasHours(DateOnly day) =>
            IsUrgent ? UrgentWindow is not null : _weekly[day.DayOfWeek].Any() || _extraByDay[day].Any();

        public int JobsOn(DateOnly day) => _jobsByDay.GetValueOrDefault(day);

        public bool IsDayFull(DateOnly day) => JobsOn(day) >= Rules.MaxJobsPerDay;

        public bool IsOnTimeOff(DateOnly day) => TimeOff.Any(range => range.From <= day && day <= range.To);

        public bool IsClosedDay(DateOnly day) => ClosedDays.Contains(day);
    }
}
