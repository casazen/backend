using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>ADR, RevPAR, the comparison with the same days one year earlier, and the trend of the reports of the Home (NF-96).</summary>
/// <remarks>
/// They read the same rows through the same code as the Home and as the reports of <c>HostDashboardService.Reports.cs</c>: the revenue
/// is <c>RevenueStays</c> (the confirmed stays with a night in the period) priced by <see cref="StayKpiRules"/>, the nights are the
/// ones <c>GetNightSetsByPropertyAsync</c> counts for the Home's occupancy. So the total of a report is the number the Home gives for
/// the same days, a week of a trend is a part of the period and the weeks add up to it. A read is a fixed number of queries whatever the
/// number of properties (the active properties, the stays, the bookings and the blocks that make the nights, and the names of the other
/// properties with revenue), once more for the year before. Money is read by the web layer only for a caller with <c>payment.read</c>.
/// </remarks>
public sealed partial class HostDashboardService
{
    public async Task<HostPerformanceReport> GetPerformanceAsync(
        HostScope scope,
        HostPerformanceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var data = await LoadPerformanceAsync(
            scope, query.From, query.To, query.PropertyId, query.PropertyIds, query.CompareYear, cancellationToken);

        IReadOnlyList<HostPerformanceRow> rows = query.GroupBy switch
        {
            HostPerformanceGroupBy.Property => PropertyRows(data),
            HostPerformanceGroupBy.Channel => ChannelRows(data),
            _ => [],
        };
        return new HostPerformanceReport(data.Current.Period, data.Previous?.Period, rows, TotalRow(data));
    }

    public async Task<HostTrendReport> GetTrendAsync(
        HostScope scope,
        HostTrendQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var data = await LoadPerformanceAsync(
            scope, query.From, query.To, query.PropertyId, query.PropertyIds, query.CompareYear, cancellationToken);
        var period = data.Current.Period;

        // The weeks or months of the Home's chart, cut at the edges of the period, and the same days one year earlier.
        var (unit, buckets) = HostReportRules.TrendBucketsOf(period, query.Bucket, data.Previous is not null);
        var windows = buckets.Select(bucket => (bucket.From, bucket.To)).ToList();
        var previousWindows = data.Previous is null
            ? null
            : buckets.Select(bucket => (bucket.PreviousYearFrom!.Value, bucket.PreviousYearTo!.Value)).ToList();

        // The most revenue first, at most MaxSeries lines (25 unless asked otherwise): the rest is summed in one more, never dropped.
        var sources = TrendSources(query.GroupBy, data)
            .Select(source => (Source: source, Revenue: StayKpiRules.RevenueOf(source.Stays, period.From, period.To)))
            .OrderByDescending(item => item.Revenue)
            .ThenBy(item => item.Source.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Source.Key, StringComparer.Ordinal)
            .Select(item => item.Source)
            .ToList();
        var most = Math.Max(2, query.MaxSeries);
        if (sources.Count > most)
        {
            var kept = sources.Take(most - 1).ToList();
            kept.Add(MergedSource(sources.Skip(most - 1).ToList()));
            sources = kept;
        }

        var series = sources
            .Select(source => BuildSeries(source, data, buckets, windows, previousWindows))
            .ToList();
        return new HostTrendReport(period, data.Previous?.Period, buckets, series) { Unit = unit };
    }

    // ---------------------------------------------------------------- what is read

    // A property of the report: the active short-rent ones of the scope and the others that have revenue in a period.
    private sealed record PerformanceProperty(Guid Id, string Name, DateTime CreatedAt);

    private sealed record PerformanceStay(Guid PropertyId, HostRevenueStay Stay);

    // The stays and the nights of one period.
    private sealed record PerformancePeriod(
        HostDashboardPeriod Period,
        IReadOnlyList<PerformanceStay> Stays,
        IReadOnlyDictionary<Guid, PropertyNights> Nights);

    private sealed record PerformanceData(
        PerformancePeriod Current,
        PerformancePeriod? Previous,
        IReadOnlyList<PerformanceProperty> Properties);

    private async Task<PerformanceData> LoadPerformanceAsync(
        HostScope scope,
        DateOnly firstDay,
        DateOnly lastDay,
        Guid? propertyId,
        IReadOnlyCollection<Guid>? onlyThese,
        bool compareYear,
        CancellationToken cancellationToken)
    {
        if (HostReportRules.CheckPeriod(firstDay, lastDay) == HostReportPeriodCheck.Invalid)
            throw new ArgumentException("The days are not in order or out of the supported years.", nameof(firstDay));

        var period = HostReportRules.PeriodOf(firstDay, lastDay);
        var previousPeriod = compareYear ? HostReportRules.PreviousYearPeriodOf(firstDay, lastDay) : null;

        // One 404 for a property that is missing, another org's, not reached by the caller, or not one of the properties this read is
        // narrowed to: nothing tells them apart.
        await EnsurePropertyInScopeAsync(scope, propertyId, cancellationToken);
        if (propertyId is { } asked && onlyThese is not null && !onlyThese.Contains(asked))
            throw new NotFoundException($"Property {asked} not found") { MessageKey = "PropertyNotFound" };

        var ids = onlyThese?.Distinct().ToList();
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var properties = new List<PerformanceProperty>();
        if (ids is not { Count: 0 })
        {
            var active = ActivePropertiesInScope(scope, propertyId);
            if (ids is not null)
                active = active.Where(p => ids.Contains(p.Id));
            properties.AddRange(await active
                .Select(p => new PerformanceProperty(p.Id, p.Name, p.CreatedAt))
                .ToListAsync(cancellationToken));
        }

        var activeIds = properties.Select(p => p.Id).ToList();
        var current = await LoadPeriodAsync(scope, propertyId, ids, activeIds, period, nowUtc, cancellationToken);
        var previous = previousPeriod is null
            ? null
            : await LoadPeriodAsync(scope, propertyId, ids, activeIds, previousPeriod, nowUtc, cancellationToken);

        // A paused or deleted property still has its stays and its money: it is a row of its own, by the name it had.
        var known = activeIds.ToHashSet();
        var others = current.Stays
            .Concat(previous?.Stays ?? [])
            .Select(s => s.PropertyId)
            .Where(id => !known.Contains(id))
            .Distinct()
            .ToList();
        if (others.Count > 0)
        {
            properties.AddRange(await db.Properties
                .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
                .AsNoTracking()
                .Where(p => p.OrgId == scope.OrgId && others.Contains(p.Id))
                .InScope(scope)
                .Select(p => new PerformanceProperty(p.Id, p.Name, p.CreatedAt))
                .ToListAsync(cancellationToken));
        }

        var ordered = properties
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .ToList();
        return new PerformanceData(current, previous, ordered);
    }

    private async Task<PerformancePeriod> LoadPeriodAsync(
        HostScope scope,
        Guid? propertyId,
        List<Guid>? onlyThese,
        IReadOnlyCollection<Guid> activeIds,
        HostDashboardPeriod period,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (onlyThese is { Count: 0 })
            return new PerformancePeriod(period, [], new Dictionary<Guid, PropertyNights>());

        var stays = RevenueStays(scope, propertyId, period);
        if (onlyThese is not null)
            stays = stays.Where(b => onlyThese.Contains(b.PropertyId));

        var rows = await stays
            .Select(b => new { b.PropertyId, b.BasePrice, b.CheckInDate, b.CheckOutDate, b.Source })
            .ToListAsync(cancellationToken);
        var nights = await GetNightSetsByPropertyAsync(activeIds, period, nowUtc, cancellationToken);
        return new PerformancePeriod(
            period,
            rows.Select(r => new PerformanceStay(r.PropertyId, new HostRevenueStay(r.BasePrice, r.CheckInDate, r.CheckOutDate, r.Source))).ToList(),
            nights);
    }

    // ---------------------------------------------------------------- the figures

    // The figures of some stays and of the nights of some properties, for the days from `from` (included) to `to` (excluded). The
    // revenue is the Home's: the stays with a night there, priced pro rata by StayKpiRules and rounded once, on the sum. Without
    // nights (a channel) the figures say so: a night available belongs to a property.
    private static HostPerformanceFigures FiguresOf(
        IEnumerable<HostRevenueStay> stays,
        IReadOnlyCollection<PropertyNights>? nights,
        DateTime from,
        DateTime to)
    {
        var counted = StayKpiRules.WithNightsIn(stays, from, to).ToList();
        var revenue = StayKpiRules.RevenueOf(counted, from, to);
        var revenueNights = counted.Sum(s => PropertyOccupancy.NightsIn(s.CheckIn, s.CheckOut, from, to).Count());
        if (nights is null)
            return new HostPerformanceFigures(0, 0, 0, revenue, counted.Count, revenueNights) { HasNights = false };

        var days = (to - from).Days;
        int occupied = 0, closed = 0, available = 0;
        foreach (var property in nights)
        {
            var taken = property.Taken.Count(night => night >= from && night < to);
            var shut = property.ClosedOnly.Count(night => night >= from && night < to);
            occupied += taken;
            closed += shut;
            available += days - shut;
        }

        return new HostPerformanceFigures(occupied, available, closed, revenue, counted.Count, revenueNights);
    }

    // The figures of a year before, or nothing when there was nothing to compare with: a property that did not exist yet, a channel
    // without a stay, a scope with nothing available. Zeros would pass for a result.
    private static HostPerformanceFigures? PreviousYearFigures(HostPerformanceFigures figures, bool existed) =>
        figures.StayCount == 0 && (figures.AvailableNights == 0 || !existed) ? null : figures;

    // The property existed by the last day of the window ending (excluded) at `to`: it was created on that day or before, in Rome.
    private static bool ExistedBefore(DateTime createdAt, DateTime to) =>
        RomeCalendar.DateInRome(createdAt) < DateOnly.FromDateTime(to);

    private static IReadOnlyCollection<PropertyNights> NightsOf(PerformancePeriod data, Guid propertyId) =>
        data.Nights.TryGetValue(propertyId, out var nights) ? [nights] : [];

    private static List<HostPerformanceRow> PropertyRows(PerformanceData data)
    {
        var current = data.Current;
        var previous = data.Previous;
        var staysOf = current.Stays.ToLookup(s => s.PropertyId, s => s.Stay);
        var previousStaysOf = previous?.Stays.ToLookup(s => s.PropertyId, s => s.Stay);

        var rows = new List<HostPerformanceRow>(data.Properties.Count);
        foreach (var property in data.Properties)
        {
            var figures = FiguresOf(staysOf[property.Id], NightsOf(current, property.Id), current.Period.From, current.Period.To);
            HostPerformanceFigures? before = null;
            if (previous is not null && previousStaysOf is not null)
            {
                before = PreviousYearFigures(
                    FiguresOf(previousStaysOf[property.Id], NightsOf(previous, property.Id), previous.Period.From, previous.Period.To),
                    ExistedBefore(property.CreatedAt, previous.Period.To));
            }

            rows.Add(new HostPerformanceRow(property.Id.ToString("D"), property.Id, property.Name, figures, before));
        }

        return rows;
    }

    private static List<HostPerformanceRow> ChannelRows(PerformanceData data)
    {
        var current = data.Current;
        var previous = data.Previous;
        var staysOf = current.Stays.ToLookup(s => s.Stay.Source, s => s.Stay);
        var previousStaysOf = previous?.Stays.ToLookup(s => s.Stay.Source, s => s.Stay);
        var sources = current.Stays.Select(s => s.Stay.Source)
            .Concat(previous?.Stays.Select(s => s.Stay.Source) ?? [])
            .Distinct()
            .ToList();

        var rows = new List<HostPerformanceRow>(sources.Count);
        foreach (var source in sources)
        {
            var figures = FiguresOf(staysOf[source], null, current.Period.From, current.Period.To);
            HostPerformanceFigures? before = null;
            if (previous is not null && previousStaysOf is not null)
                before = PreviousYearFigures(FiguresOf(previousStaysOf[source], null, previous.Period.From, previous.Period.To), true);

            rows.Add(new HostPerformanceRow(source.ToString(), null, source.ToString(), figures, before));
        }

        return rows
            .OrderByDescending(r => r.Figures.Revenue)
            .ThenByDescending(r => r.Figures.StayCount)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
    }

    // The whole scope: all the stays at once and the nights of every active property, which is what the Home counts.
    private static HostPerformanceRow TotalRow(PerformanceData data)
    {
        var current = data.Current;
        var figures = FiguresOf(
            current.Stays.Select(s => s.Stay), current.Nights.Values.ToList(), current.Period.From, current.Period.To);
        HostPerformanceFigures? before = null;
        if (data.Previous is { } previous)
        {
            before = PreviousYearFigures(
                FiguresOf(previous.Stays.Select(s => s.Stay), previous.Nights.Values.ToList(), previous.Period.From, previous.Period.To),
                true);
        }

        return new HostPerformanceRow(HostPerformanceRules.TotalKey, null, HostPerformanceRules.TotalKey, figures, before);
    }

    // ---------------------------------------------------------------- the trend

    // The stays and the nights of one line of a trend, in both years. `CreatedAt` is set for a line that is one property.
    private sealed record TrendSource(
        string Key,
        string Label,
        Guid? PropertyId,
        IReadOnlyList<HostRevenueStay> Stays,
        IReadOnlyList<HostRevenueStay> PreviousStays,
        IReadOnlyCollection<PropertyNights>? Nights,
        IReadOnlyCollection<PropertyNights>? PreviousNights,
        DateTime? CreatedAt);

    private static List<TrendSource> TrendSources(HostPerformanceGroupBy groupBy, PerformanceData data)
    {
        var current = data.Current;
        var previous = data.Previous;
        var staysOf = current.Stays.ToLookup(s => s.PropertyId, s => s.Stay);
        var previousStaysOf = previous?.Stays.ToLookup(s => s.PropertyId, s => s.Stay);

        switch (groupBy)
        {
            case HostPerformanceGroupBy.Property:
                return data.Properties
                    .Select(property => new TrendSource(
                        property.Id.ToString("D"),
                        property.Name,
                        property.Id,
                        staysOf[property.Id].ToList(),
                        previousStaysOf?[property.Id].ToList() ?? [],
                        NightsOf(current, property.Id),
                        previous is null ? null : NightsOf(previous, property.Id),
                        property.CreatedAt))
                    .ToList();

            case HostPerformanceGroupBy.Channel:
                var byChannel = current.Stays.ToLookup(s => s.Stay.Source, s => s.Stay);
                var previousByChannel = previous?.Stays.ToLookup(s => s.Stay.Source, s => s.Stay);
                return byChannel.Select(g => g.Key)
                    .Concat(previousByChannel?.Select(g => g.Key) ?? [])
                    .Distinct()
                    .Select(source => new TrendSource(
                        source.ToString(),
                        source.ToString(),
                        null,
                        byChannel[source].ToList(),
                        previousByChannel?[source].ToList() ?? [],
                        null,
                        null,
                        null))
                    .ToList();

            default:
                return
                [
                    new TrendSource(
                        HostPerformanceRules.TotalKey,
                        HostPerformanceRules.TotalKey,
                        null,
                        current.Stays.Select(s => s.Stay).ToList(),
                        previous?.Stays.Select(s => s.Stay).ToList() ?? [],
                        current.Nights.Values.ToList(),
                        previous?.Nights.Values.ToList(),
                        null),
                ];
        }
    }

    // What does not fit in the lines of a trend, as one more line.
    private static TrendSource MergedSource(IReadOnlyList<TrendSource> sources) => new(
        HostPerformanceRules.OtherSeriesKey,
        HostPerformanceRules.OtherSeriesKey,
        null,
        sources.SelectMany(s => s.Stays).ToList(),
        sources.SelectMany(s => s.PreviousStays).ToList(),
        sources.All(s => s.Nights is null) ? null : sources.SelectMany(s => s.Nights ?? []).ToList(),
        sources.All(s => s.PreviousNights is null) ? null : sources.SelectMany(s => s.PreviousNights ?? []).ToList(),
        null);

    private static HostTrendSeries BuildSeries(
        TrendSource source,
        PerformanceData data,
        IReadOnlyList<HostTrendBucket> buckets,
        IReadOnlyList<(DateTime From, DateTime To)> windows,
        IReadOnlyList<(DateTime From, DateTime To)>? previousWindows)
    {
        var current = FiguresByWindow(source.Stays, source.Nights, data.Current.Period, windows);
        var points = current.Select((figures, index) => new HostTrendPoint(buckets[index].Label, figures)).ToList();

        var previousPoints = new List<HostTrendPoint?>();
        if (data.Previous is { } previous && previousWindows is not null)
        {
            var before = FiguresByWindow(source.PreviousStays, source.PreviousNights, previous.Period, previousWindows);
            for (var index = 0; index < before.Count; index++)
            {
                // A line that is one property had nothing to compare with in a bucket when it did not exist yet.
                var existed = source.CreatedAt is not { } created || ExistedBefore(created, previousWindows[index].To);
                previousPoints.Add(PreviousYearFigures(before[index], existed) is { } kept
                    ? new HostTrendPoint(buckets[index].Label, kept)
                    : null);
            }
        }

        return new HostTrendSeries(source.Key, source.Label, points, previousPoints) { PropertyId = source.PropertyId };
    }

    // The figures of each window of a period. The nights are counted for the days of the window; the revenue is handed out so that the
    // windows add up to the revenue of the period to the cent (the carry of the Home's chart) and none is ever negative.
    private static List<HostPerformanceFigures> FiguresByWindow(
        IReadOnlyList<HostRevenueStay> stays,
        IReadOnlyCollection<PropertyNights>? nights,
        HostDashboardPeriod period,
        IReadOnlyList<(DateTime From, DateTime To)> windows)
    {
        var amounts = DistributeRevenue(stays, windows, StayKpiRules.RevenueOf(stays, period.From, period.To));
        var figures = new List<HostPerformanceFigures>(windows.Count);
        for (var index = 0; index < windows.Count; index++)
            figures.Add(FiguresOf(stays, nights, windows[index].From, windows[index].To) with { Revenue = amounts[index] });

        return figures;
    }

    // A window of no days (the day of the leap year with no twin in the year before) has no revenue: the cents left by the rounding
    // land on the last window that has days.
    private static decimal[] DistributeRevenue(
        IReadOnlyList<HostRevenueStay> stays,
        IReadOnlyList<(DateTime From, DateTime To)> windows,
        decimal total)
    {
        var amounts = new decimal[windows.Count];
        var withDays = windows
            .Select((window, index) => (Window: window, Index: index))
            .Where(item => item.Window.To > item.Window.From)
            .ToList();
        if (withDays.Count == 0)
            return amounts;

        var shares = HostOverviewSeriesRules.Distribute(stays, withDays.Select(item => item.Window).ToList(), total);
        for (var position = 0; position < withDays.Count; position++)
            amounts[withDays[position].Index] = shares[position];

        return amounts;
    }
}
