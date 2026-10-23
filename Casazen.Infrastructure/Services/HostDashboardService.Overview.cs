using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>The numbers of the Home (SR-03b): the figures of the KPIs, compared and cut into the bars of a chart.</summary>
/// <remarks>
/// It reads the figures through the code of <see cref="GetKpisAsync(HostScope, HostDashboardQuery, CancellationToken)"/>
/// (<c>GetFiguresWithStaysAsync</c>: occupancy, revenue, cash, direct share), once for the period and once for the one before, so
/// the two never differ in a rule. What it adds is bounded by the period too: one read of the stays that arrive in this and the
/// previous period, one of the next three arrivals; the chart is made of the stays the revenue already read. Nothing here loops
/// over the properties.
/// </remarks>
public sealed partial class HostDashboardService
{
    /// <summary>Arrivals listed as "the next ones" (the Home shows three).</summary>
    public const int NextArrivalsSize = 3;

    public async Task<HostOverview> GetOverviewAsync(
        HostScope scope,
        HostOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var now = _clock.GetUtcNow();
        var today = RomeCalendar.TodayAt(now);
        var periodQuery = new HostDashboardQuery(
            query.Kind, query.Month, query.PropertyId, Compare: true, IncludeCollected: query.IncludeMoney, From: query.From, To: query.To);
        var period = PeriodOf(periodQuery, today);
        var previousPeriod = period.Previous();

        // One 404 for a property that is missing, another org's or not reached by the caller: nothing tells them apart.
        if (query.PropertyId is { } filtered && !await IsPropertyInScopeAsync(scope, filtered, cancellationToken))
            throw new NotFoundException($"Property {filtered} not found") { MessageKey = "PropertyNotFound" };

        var propertyIds = await ActivePropertiesInScope(scope, query.PropertyId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var (current, stays) = await GetFiguresWithStaysAsync(scope, periodQuery, propertyIds, period, now.UtcDateTime, cancellationToken);
        var (before, previousStays) = await GetFiguresWithStaysAsync(
            scope, periodQuery, propertyIds, previousPeriod, now.UtcDateTime, cancellationToken);
        var upcoming = await GetUpcomingAsync(scope, query.PropertyId, period, previousPeriod, now.UtcDateTime, cancellationToken);
        var nextArrivals = await GetNextArrivalsAsync(scope, query.PropertyId, today, now.UtcDateTime, cancellationToken);

        HostOverviewRevenue? revenue = null;
        HostOverviewRevenue? previousRevenue = null;
        HostOverviewSeries? series = null;
        if (query.IncludeMoney)
        {
            revenue = new HostOverviewRevenue(
                current.Revenue, current.RevenueStayCount, HostOverviewRules.RevenueDeltaPercent(current.Revenue, before.Revenue));
            previousRevenue = new HostOverviewRevenue(before.Revenue, before.RevenueStayCount, null);
            series = HostOverviewSeriesRules.Build(period, today, stays, current.Revenue, previousStays, before.Revenue);
        }

        return new HostOverview(
            period,
            today,
            propertyIds.Count,
            new HostOverviewOccupancy(current.Occupancy, HostOverviewRules.OccupancyDeltaPoints(current.Occupancy, before.Occupancy)),
            revenue,
            current.Collected,
            current.DirectShare,
            upcoming,
            nextArrivals,
            new HostOverviewPrevious(
                previousPeriod,
                new HostOverviewOccupancy(before.Occupancy, null),
                previousRevenue,
                before.Collected,
                before.DirectShare),
            series);
    }

    // The stays that arrive in the period and in the one before: confirmed stays, and the "pay at the property" requests the host has
    // still to answer (which StayKpiRules leaves out of every KPI). Bounded by the two periods; counted here, by the Rome day.
    private async Task<HostOverviewUpcoming> GetUpcomingAsync(
        HostScope scope,
        Guid? propertyId,
        HostDashboardPeriod period,
        HostDashboardPeriod previousPeriod,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var arrivals = await BookingsInScope(scope, propertyId)
            .Where(StayKpiRules.ArrivesBetween(previousPeriod.From, period.To))
            .Where(BookingListRules.Or(StayKpiRules.IsConfirmedStay(), OnSiteRequests.IsAwaitingHostApproval(nowUtc)))
            .Select(b => new { b.CheckInDate, b.Status })
            .ToListAsync(cancellationToken);

        var days = arrivals
            .Select(a => (Day: StayKpiRules.RomeDateOf(a.CheckInDate), Awaiting: a.Status == BookingStatus.Pending))
            .ToList();
        var inPeriod = days.Where(a => a.Day >= period.From && a.Day < period.To).ToList();
        return new HostOverviewUpcoming(
            inPeriod.Count,
            days.Count(a => a.Day >= previousPeriod.From && a.Day < previousPeriod.To),
            inPeriod.Count(a => a.Awaiting));
    }

    // The next arrivals from today on, whatever the period: the soonest confirmed stays and requests waiting for the host.
    private async Task<IReadOnlyList<HostDashboardStay>> GetNextArrivalsAsync(
        HostScope scope,
        Guid? propertyId,
        DateTime today,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var start = RomeCalendar.StartOfDayUtc(today);
        var next = await BookingsInScope(scope, propertyId)
            .Where(b => b.CheckInDate >= start)
            .Where(BookingListRules.Or(b => b.Status == BookingStatus.Confirmed, OnSiteRequests.IsAwaitingHostApproval(nowUtc)))
            .OrderBy(b => b.CheckInDate)
            .ThenBy(b => b.Id)
            .Take(NextArrivalsSize)
            .Select(StayProjection)
            .ToListAsync(cancellationToken);
        return next.Select(ToRomeDates).ToList();
    }

    private static HostDashboardPeriod CustomPeriodOf(HostDashboardQuery query) =>
        query.From is { } from && query.To is { } to && HostDashboardPeriod.TryCustom(from, to, out var period)
            ? period
            : throw new ArgumentException(
                $"A free period needs its first and last day, from 1 to {HostDashboardPeriod.MaxCustomDays} days.", nameof(query));
}
