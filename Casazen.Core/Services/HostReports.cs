using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// What a report asks of <see cref="IHostDashboardService"/> (SR-10): the days of the period, both included, as Europe/Rome calendar
/// dates, and optionally one property (it must be one the scope reaches).
/// </summary>
/// <param name="From">First day of the period, included.</param>
/// <param name="To">Last day of the period, included.</param>
/// <param name="PropertyId">Only this property; null = every property of the scope.</param>
public sealed record HostReportQuery(DateOnly From, DateOnly To, Guid? PropertyId = null);

public enum HostReportPeriodCheck
{
    Ok,

    Invalid,

    TooLong,
}

public static class HostReportRules
{
    public const int MaxMonths = 24;

    public static HostReportPeriodCheck CheckPeriod(DateOnly firstDay, DateOnly lastDay)
    {
        if (lastDay < firstDay
            || firstDay.Year < HostDashboardPeriod.MinCustomYear
            || lastDay.Year > HostDashboardPeriod.MaxCustomYear)
            return HostReportPeriodCheck.Invalid;

        return lastDay > firstDay.AddMonths(MaxMonths).AddDays(-1) ? HostReportPeriodCheck.TooLong : HostReportPeriodCheck.Ok;
    }

    public static HostDashboardPeriod PeriodOf(DateOnly firstDay, DateOnly lastDay)
    {
        var from = firstDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return new HostDashboardPeriod(HostDashboardPeriodKind.Custom, from, from.AddDays(lastDay.DayNumber - firstDay.DayNumber + 1));
    }

    public const int MaxWeeklyTrendDays = 92;

    public static HostOverviewSeriesUnit TrendUnitFor(HostDashboardPeriod period, HostTrendBucketUnit requested)
    {
        ArgumentNullException.ThrowIfNull(period);

        return requested switch
        {
            HostTrendBucketUnit.Week => HostOverviewSeriesUnit.Week,
            HostTrendBucketUnit.Month => HostOverviewSeriesUnit.Month,
            _ => HostOverviewSeriesRules.UnitFor(period, MaxWeeklyTrendDays),
        };
    }

    public static (DateOnly First, DateOnly Last) PreviousYear(DateOnly firstDay, DateOnly lastDay) =>
        (firstDay.AddYears(-1), lastDay.AddYears(-1));

    public static HostDashboardPeriod PreviousYearPeriodOf(DateOnly firstDay, DateOnly lastDay)
    {
        var (first, last) = PreviousYear(firstDay, lastDay);
        return PeriodOf(first, last);
    }

    public static IReadOnlyList<(DateTime From, DateTime To)> PreviousYearBuckets(IReadOnlyList<(DateTime From, DateTime To)> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        var moved = new List<(DateTime From, DateTime To)>(buckets.Count);
        DateTime? endedOn = null;
        foreach (var bucket in buckets)
        {
            var from = endedOn ?? bucket.From.AddYears(-1);
            var to = bucket.To.AddDays(-1).AddYears(-1).AddDays(1);
            if (to < from)
                to = from;

            moved.Add((from, to));
            endedOn = to;
        }

        return moved;
    }

    public static (HostOverviewSeriesUnit Unit, IReadOnlyList<HostTrendBucket> Buckets) TrendBucketsOf(
        HostDashboardPeriod period, HostTrendBucketUnit requested, bool compareYear)
    {
        ArgumentNullException.ThrowIfNull(period);

        var unit = TrendUnitFor(period, requested);
        var spans = HostOverviewSeriesRules.BucketsOf(period, unit);
        var previous = compareYear ? PreviousYearBuckets(spans.Select(span => (span.From, span.To)).ToList()) : null;
        var buckets = spans
            .Select((span, index) => new HostTrendBucket(span.Label, span.From, span.To, previous?[index].From, previous?[index].To))
            .ToList();
        return (unit, buckets);
    }
}

public sealed record HostRevenueByPropertyRow(Guid PropertyId, string PropertyName, decimal Amount, int StayCount, int Nights);

public sealed record HostRevenueByProperty(
    HostDashboardPeriod Period,
    IReadOnlyList<HostRevenueByPropertyRow> Rows,
    decimal Amount,
    int StayCount,
    int Nights);

public sealed record HostOccupancyByPropertyRow(Guid PropertyId, string PropertyName, HostDashboardOccupancy Occupancy);

public sealed record HostOccupancyByProperty(
    HostDashboardPeriod Period,
    IReadOnlyList<HostOccupancyByPropertyRow> Rows,
    HostDashboardOccupancy Total);

public sealed record HostBookingsByChannelRow(BookingSource Source, int BookingCount, int Nights, decimal Revenue);

public sealed record HostBookingsByChannel(
    HostDashboardPeriod Period,
    IReadOnlyList<HostBookingsByChannelRow> Rows,
    int BookingCount,
    int Nights,
    decimal Revenue);
