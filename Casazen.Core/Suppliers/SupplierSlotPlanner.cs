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
/// </remarks>
public static class SupplierSlotPlanner
{
    /// <summary>The longest range <see cref="PlanRange"/> accepts, in days (a year and a leap day).</summary>
    public const int MaxRangeDays = 366;

    /// <summary>The plan of one Europe/Rome day.</summary>
    public static SupplierDayPlan PlanDay(DateOnly day, SupplierPlanningInput input, SupplierSlotQuery query) =>
        PlanRange(day, day, input, query)[0];

    /// <summary>
    /// The plan of every Europe/Rome day from <paramref name="from"/> to <paramref name="to"/> (both included), in date
    /// order. At most <see cref="MaxRangeDays"/> days.
    /// </summary>
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
        var capacity = Math.Max(1, context.Rules.ParallelJobs);
        var duration = TimeSpan.FromMinutes(context.Query.DurationMinutes);
        var step = TimeSpan.FromMinutes(context.Rules.SlotStepMinutes);

        // Only what can touch this day: a stretch that ends before the day starts or starts after it ends cannot overlap a slot.
        var nearby = context.Widened
            .Where(interval => interval.EndUtc > dayStartUtc && interval.StartUtc < dayEndUtc)
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

    /// <summary>The first reason the day has no slot, or <c>null</c> when it is open (it may still have no free slot).</summary>
    private static SupplierDayClosure? ClosureOf(DateOnly day, PlanningContext context)
    {
        if (day < context.Today)
            return SupplierDayClosure.Past;

        if (context.TimeOff.Any(range => range.From <= day && day <= range.To))
            return SupplierDayClosure.TimeOff;

        if (context.ClosedDays.Contains(day))
            return SupplierDayClosure.DayClosed;

        if (context.Query.WeekdaysMask is { } mask && !SupplierServiceWeekdays.IsOffered(mask, day.DayOfWeek))
            return SupplierDayClosure.ServiceNotOffered;

        if (!context.HasHours(day))
            return SupplierDayClosure.NoHours;

        if (context.JobsOn(day) >= context.Rules.MaxJobsPerDay)
            return SupplierDayClosure.MaxJobsReached;

        if (RomeCalendar.StartOfDayUtc(day.AddDays(1)) <= context.EarliestStartUtc)
            return SupplierDayClosure.WithinNotice;

        if (day > context.Today.AddDays(context.Rules.HorizonDays))
            return SupplierDayClosure.BeyondHorizon;

        return null;
    }

    /// <summary>
    /// The continuous bands of the day in UTC, in start order: the weekly bands of the weekday and the extra openings of the
    /// day, bands that touch or overlap joined into one. A band that the daylight saving change leaves with no length (it
    /// starts in the skipped hour and ends right after it) is dropped.
    /// </summary>
    private static List<SupplierInterval> BandsOf(DateOnly day, PlanningContext context)
    {
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

    /// <summary>
    /// True when the slot <c>[start, end)</c> can take one more job: at no instant of it are there as many widened stretches
    /// as the supplier can do at once. With a capacity of 1 that is "no stretch overlaps it".
    /// </summary>
    private static bool HasRoom(DateTime start, DateTime end, IReadOnlyList<SupplierInterval> widened, int capacity)
    {
        var overlapping = widened.Where(interval => interval.StartUtc < end && interval.EndUtc > start).ToList();
        if (overlapping.Count < capacity)
            return true;

        // The number of stretches that overlap a given instant only goes up where one starts: look at the start of the slot
        // and at the start of every stretch inside it.
        foreach (var instant in overlapping.Select(interval => interval.StartUtc > start ? interval.StartUtc : start).Distinct())
        {
            var concurrent = overlapping.Count(interval => interval.StartUtc <= instant && instant < interval.EndUtc);
            if (concurrent >= capacity)
                return false;
        }

        return true;
    }

    /// <summary>The input of a plan, indexed once for all its days.</summary>
    private sealed class PlanningContext
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

            var nowUtc = UtcDateTime.Normalize(input.NowUtc);
            Today = DateOnly.FromDateTime(RomeCalendar.TodayAt(new DateTimeOffset(nowUtc)));
            EarliestStartUtc = nowUtc.AddHours(Math.Max(0, query.MinNoticeHours ?? input.Rules.MinNoticeHours));

            _weekly = input.WeeklyHours
                .Where(band => band.EndMinute > band.StartMinute)
                .OrderBy(band => band.StartMinute)
                .ToLookup(band => band.Weekday);
            _extraByDay = input.ExtraOpenings
                .Where(opening => opening.EndUtc > opening.StartUtc)
                .ToLookup(opening => RomeCalendar.DateInRome(opening.StartUtc));

            var widened = new List<SupplierInterval>();
            var buffer = TimeSpan.FromMinutes(Math.Max(0, input.Rules.BufferMinutes));
            foreach (var occupancy in input.Occupancies)
            {
                // A hold that lapsed no longer holds a place (the planner is given the instant it is judged at).
                if (occupancy.Kind == SupplierOccupancyKind.Hold && occupancy.ExpiresAtUtc <= nowUtc)
                    continue;

                if (occupancy.CountsTowardsDailyMax)
                {
                    var day = occupancy.Day
                        ?? (occupancy.StartUtc is { } started ? RomeCalendar.DateInRome(started) : (DateOnly?)null);
                    if (day is { } counted)
                        _jobsByDay[counted] = _jobsByDay.GetValueOrDefault(counted) + 1;
                }

                if (occupancy is { StartUtc: { } startUtc, EndUtc: { } endUtc })
                    widened.Add(new SupplierInterval(startUtc - buffer, endUtc + buffer));
            }

            Widened = widened;
        }

        public SupplierSlotQuery Query { get; }

        public SupplierPlanningRules Rules { get; }

        public IReadOnlyList<SupplierDateRange> TimeOff { get; }

        public IReadOnlySet<DateOnly> ClosedDays { get; }

        /// <summary>Today in Europe/Rome.</summary>
        public DateOnly Today { get; }

        /// <summary>The earliest instant a slot may start: now plus the notice.</summary>
        public DateTime EarliestStartUtc { get; }

        /// <summary>What takes the supplier's time, each stretch widened by the buffer on both sides.</summary>
        public IReadOnlyList<SupplierInterval> Widened { get; }

        public IEnumerable<SupplierWeeklyBand> WeeklyBands(DayOfWeek weekday) => _weekly[weekday];

        public IEnumerable<SupplierInterval> ExtraOpeningsOn(DateOnly day) => _extraByDay[day];

        public bool HasHours(DateOnly day) => _weekly[day.DayOfWeek].Any() || _extraByDay[day].Any();

        public int JobsOn(DateOnly day) => _jobsByDay.GetValueOrDefault(day);
    }
}
