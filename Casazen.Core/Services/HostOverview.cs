using System.Globalization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// What the Home asks of <see cref="IHostDashboardService.GetOverviewAsync"/> (SR-03b). Everything but <see cref="Kind"/> is
/// optional; the web layer checks the days of a free period before it gets here.
/// </summary>
/// <param name="Kind">The kind of period.</param>
/// <param name="Month">The month of <see cref="HostDashboardPeriodKind.Month"/>; the current Europe/Rome month when null.</param>
/// <param name="From">First day of a <see cref="HostDashboardPeriodKind.Custom"/> period, included.</param>
/// <param name="To">Last day of a <see cref="HostDashboardPeriodKind.Custom"/> period, included.</param>
/// <param name="PropertyId">Only this property (it must be one the scope reaches); null = every property of the scope.</param>
/// <param name="IncludeMoney">
/// Also give the revenue, the money collected and the revenue by week or month. Money is payment data: the web layer sets it only
/// for a caller who may read payments (<c>payment.read</c>, SR-D7), so the default is to leave it out.
/// </param>
public sealed record HostOverviewQuery(
    HostDashboardPeriodKind Kind = HostDashboardPeriodKind.Month,
    DateOnly? Month = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? PropertyId = null,
    bool IncludeMoney = false);

/// <summary>A confirmed stay of the revenue of a period: the rows the revenue and its series are made of.</summary>
/// <param name="BasePrice">Lodging plus cleaning, tourist tax excluded (<see cref="Booking.BasePrice"/>).</param>
/// <param name="CheckIn">Check-in date, midnight UTC of the stay date.</param>
/// <param name="CheckOut">Check-out date, midnight UTC of the stay date.</param>
public sealed record HostRevenueStay(decimal BasePrice, DateTime CheckIn, DateTime CheckOut, BookingSource Source);

/// <summary>Revenue of a period, by accrual (the nights of the confirmed stays), and how it compares with the period before.</summary>
/// <param name="Amount">Euros, rounded to the cent.</param>
/// <param name="StayCount">Confirmed stays with at least one night in the period.</param>
/// <param name="DeltaPercent">
/// How much more (or less) than the period before, in percent to one decimal; null for the period before itself and when that had
/// no revenue to compare with.
/// </param>
public sealed record HostOverviewRevenue(decimal Amount, int StayCount, decimal? DeltaPercent);

/// <summary>Occupancy of a period and how it compares with the period before.</summary>
/// <param name="Occupancy">The nights.</param>
/// <param name="DeltaPoints">
/// The occupancy rate minus the one of the period before, in percentage points to one decimal; null for the period before itself
/// and when either period has nothing available.
/// </param>
public sealed record HostOverviewOccupancy(HostDashboardOccupancy Occupancy, decimal? DeltaPoints);

/// <summary>
/// The stays that arrive in the period (SR-03b): a confirmed stay, or a "pay at the property" request the host has still to answer
/// (<see cref="OnSiteRequests.IsAwaitingHostApproval"/>), by the Europe/Rome day of the check-in. Never a cancelled booking, a checkout
/// hold or a request whose guest has not confirmed the email.
/// </summary>
/// <param name="Count">Stays that arrive in the period, requests included.</param>
/// <param name="PreviousCount">The same count over the period before.</param>
/// <param name="PendingApproval">Of <paramref name="Count"/>, the requests waiting for the host's answer.</param>
public sealed record HostOverviewUpcoming(int Count, int PreviousCount, int PendingApproval);

/// <summary>The figures of the period before (SR-03b): the same definitions, other days, nothing compared again.</summary>
/// <param name="Revenue">Null for a caller who may not read money.</param>
/// <param name="Collected">Null for a caller who may not read money.</param>
public sealed record HostOverviewPrevious(
    HostDashboardPeriod Period,
    HostOverviewOccupancy Occupancy,
    HostOverviewRevenue? Revenue,
    HostDashboardCollected? Collected,
    HostDashboardDirectShare DirectShare);

/// <summary>What is not known to the caller, absent: the money is null for a caller who may not read payments (SR-D7).</summary>
/// <param name="Revenue">Revenue of the period, by accrual; null without money.</param>
/// <param name="Collected">Money collected in the period, by cash; null without money.</param>
/// <param name="NextArrivals">The next three arrivals from today, whatever the period, soonest first (a request waiting for the host included).</param>
/// <param name="Series">Revenue by week or month, adding up to <paramref name="Revenue"/>; null without money.</param>
public sealed record HostOverview(
    HostDashboardPeriod Period,
    DateTime TodayInRome,
    int PropertyCount,
    HostOverviewOccupancy Occupancy,
    HostOverviewRevenue? Revenue,
    HostDashboardCollected? Collected,
    HostDashboardDirectShare DirectShare,
    HostOverviewUpcoming Upcoming,
    IReadOnlyList<HostDashboardStay> NextArrivals,
    HostOverviewPrevious Previous,
    HostOverviewSeries? Series);

/// <summary>The unit of the bars of the chart.</summary>
public enum HostOverviewSeriesUnit
{
    /// <summary>ISO weeks, from Monday.</summary>
    Week,

    /// <summary>Calendar months.</summary>
    Month,
}

/// <summary>A bar of the chart.</summary>
/// <param name="Label">A stable key, never text to show: the ISO week (<c>2026-W42</c>) or the month (<c>2026-10</c>).</param>
/// <param name="From">First night of the bar, midnight UTC of the date.</param>
/// <param name="To">The day after the last night of the bar, midnight UTC of the date.</param>
/// <param name="Amount">Revenue of the bar in euros.</param>
/// <param name="PreviousAmount">Revenue of the same days of the period before, in euros.</param>
/// <param name="IsFuture">The bar starts after today in Europe/Rome: what is there is a forecast of confirmed stays.</param>
public sealed record HostOverviewBucket(string Label, DateTime From, DateTime To, decimal Amount, decimal PreviousAmount, bool IsFuture);

/// <summary>Revenue by week or month of a period, next to the same days of the period before.</summary>
public sealed record HostOverviewSeries(HostOverviewSeriesUnit Unit, IReadOnlyList<HostOverviewBucket> Buckets);

/// <summary>A stretch of the period a bar of the chart covers, before it has an amount.</summary>
public sealed record HostOverviewBucketSpan(string Label, DateTime From, DateTime To);

/// <summary>
/// The differences the Home shows, worked out once on the server (SR-03b): a client never recomputes them from rounded numbers.
/// </summary>
public static class HostOverviewRules
{
    /// <summary>
    /// The occupancy rate of <paramref name="current"/> minus the one of <paramref name="previous"/>, in percentage points to one
    /// decimal; null when either has nothing available (no rate).
    /// </summary>
    public static decimal? OccupancyDeltaPoints(HostDashboardOccupancy current, HostDashboardOccupancy previous) =>
        current.Rate is { } now && previous.Rate is { } before
            ? Math.Round((now - before) * 100m, 1, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>
    /// How much more (positive) or less (negative) <paramref name="current"/> is than <paramref name="previous"/>, in percent to
    /// one decimal; null when <paramref name="previous"/> is zero (a percentage of nothing says nothing).
    /// </summary>
    public static decimal? RevenueDeltaPercent(decimal current, decimal previous) =>
        previous == 0m ? null : Math.Round((current - previous) / previous * 100m, 1, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Revenue by week or month (SR-03b): the same revenue as the KPI, cut into the bars of the chart. By accrual, like the KPI: a stay
/// pays each night of the bar it falls in with <c>BasePrice</c> / nights of the stay, so the bars add up to the revenue of the
/// period to the cent. Days are calendar dates (midnight UTC of the date): the daylight saving change of the last weekend of
/// October moves no night. Only "is the bar in the future" asks the Europe/Rome clock.
/// </summary>
public static class HostOverviewSeriesRules
{
    /// <summary>
    /// A period of up to this many days is cut by week, a longer one by month: nine bars is a chart one reads, three months of
    /// weeks (13) or a year of weeks (53) is not.
    /// </summary>
    public const int MaxWeeklyDays = 62;

    /// <summary>The unit of the bars for the period: weeks up to <see cref="MaxWeeklyDays"/> days, months beyond.</summary>
    public static HostOverviewSeriesUnit UnitFor(HostDashboardPeriod period) =>
        period.Nights <= MaxWeeklyDays ? HostOverviewSeriesUnit.Week : HostOverviewSeriesUnit.Month;

    /// <summary>
    /// The bars of the period, in order, each from its first night to the day after its last. They cover the period exactly: the
    /// first and the last bar are cut at the edges of the period, the others are whole ISO weeks (from Monday) or whole months.
    /// </summary>
    public static IReadOnlyList<HostOverviewBucketSpan> BucketsOf(HostDashboardPeriod period, HostOverviewSeriesUnit unit)
    {
        ArgumentNullException.ThrowIfNull(period);

        var spans = new List<HostOverviewBucketSpan>();
        for (var from = period.From; from < period.To;)
        {
            var next = unit == HostOverviewSeriesUnit.Week ? NextMonday(from) : FirstOfNextMonth(from);
            var to = next < period.To ? next : period.To;
            spans.Add(new HostOverviewBucketSpan(LabelOf(from, unit), from, to));
            from = to;
        }

        return spans;
    }

    /// <summary>
    /// The revenue of each bar of <paramref name="period"/> and, next to it, the one of the same days of the period before (the
    /// bars moved back by the length of the period, so they cover <c>period.Previous()</c> exactly). Each series adds up to the
    /// revenue of its period: <paramref name="revenue"/> and <paramref name="previousRevenue"/>, as the KPI gives them.
    /// </summary>
    /// <param name="todayInRome">Today in Europe/Rome (midnight UTC of the date): a bar that starts after it is in the future.</param>
    /// <param name="stays">The confirmed stays with a night in the period.</param>
    /// <param name="previousStays">The confirmed stays with a night in the period before.</param>
    public static HostOverviewSeries Build(
        HostDashboardPeriod period,
        DateTime todayInRome,
        IReadOnlyList<HostRevenueStay> stays,
        decimal revenue,
        IReadOnlyList<HostRevenueStay> previousStays,
        decimal previousRevenue)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(stays);
        ArgumentNullException.ThrowIfNull(previousStays);

        var unit = UnitFor(period);
        var spans = BucketsOf(period, unit);
        var shift = period.Nights;

        var amounts = Distribute(stays, spans.Select(span => (span.From, span.To)).ToList(), revenue);
        var previousAmounts = Distribute(
            previousStays, spans.Select(span => (span.From.AddDays(-shift), span.To.AddDays(-shift))).ToList(), previousRevenue);

        var buckets = spans
            .Select((span, index) => new HostOverviewBucket(
                span.Label, span.From, span.To, amounts[index], previousAmounts[index], span.From.Date > todayInRome.Date))
            .ToList();
        return new HostOverviewSeries(unit, buckets);
    }

    /// <summary>
    /// The revenue of the stays in each window, in euros to the cent, adding up to <paramref name="total"/> exactly. The windows
    /// must cover the period of <paramref name="total"/>. Each window gets its pro rata share (<see cref="StayKpiRules.RevenueInPeriod"/>);
    /// the rounding is carried from one window to the next (each amount is the rounded running total minus what was handed out) and
    /// the last window takes the total, so what is left over from the cents lands on the last bar and never grows beyond one cent,
    /// and no bar is ever negative.
    /// </summary>
    public static IReadOnlyList<decimal> Distribute(
        IReadOnlyList<HostRevenueStay> stays,
        IReadOnlyList<(DateTime From, DateTime To)> windows,
        decimal total)
    {
        var amounts = new decimal[windows.Count];
        var running = 0m;
        var handedOut = 0m;
        for (var index = 0; index < windows.Count; index++)
        {
            var (from, to) = windows[index];
            running += stays.Sum(stay => StayKpiRules.RevenueInPeriod(stay.BasePrice, stay.CheckIn, stay.CheckOut, from, to));
            var target = index == windows.Count - 1 ? total : Math.Round(running, 2, MidpointRounding.AwayFromZero);
            amounts[index] = target - handedOut;
            handedOut = target;
        }

        return amounts;
    }

    private static DateTime NextMonday(DateTime day)
    {
        var days = ((int)DayOfWeek.Monday - (int)day.DayOfWeek + 7) % 7;
        return day.AddDays(days == 0 ? 7 : days);
    }

    private static DateTime FirstOfNextMonth(DateTime day) =>
        new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);

    private static string LabelOf(DateTime day, HostOverviewSeriesUnit unit) => unit == HostOverviewSeriesUnit.Week
        ? $"{ISOWeek.GetYear(day)}-W{ISOWeek.GetWeekOfYear(day):00}"
        : day.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
