using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>The reports of the Home cut by property and by channel (SR-10).</summary>
/// <remarks>
/// They read the same rows through the same code as the KPIs: the revenue is <c>RevenueStays</c> (the confirmed stays with a night in
/// the period) priced by <see cref="StayKpiRules"/>, the nights are <c>GetNightsByPropertyAsync</c> (the occupancy rule of the Home,
/// which the Home sums), so the total of a report is the number the Home gives for the same days and never a second copy of a rule.
/// Money is read by the web layer only for a caller with <c>payment.read</c>. Each report is a fixed number of queries whatever the
/// number of properties: the stays (or the nights and blocks) and the names of the properties.
/// </remarks>
public sealed partial class HostDashboardService
{
    public async Task<HostRevenueByProperty> GetRevenueByPropertyAsync(
        HostScope scope,
        HostReportQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var period = HostReportRules.PeriodOf(query.From, query.To);
        await EnsurePropertyInScopeAsync(scope, query.PropertyId, cancellationToken);

        var stays = await RevenueStays(scope, query.PropertyId, period)
            .Select(b => new { b.PropertyId, b.BasePrice, b.CheckInDate, b.CheckOutDate, b.Source })
            .ToListAsync(cancellationToken);
        var staysOf = stays
            .GroupBy(s => s.PropertyId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(s => new HostRevenueStay(s.BasePrice, s.CheckInDate, s.CheckOutDate, s.Source)).ToList());

        var rows = new List<HostRevenueByPropertyRow>();
        foreach (var property in await ReportPropertiesAsync(scope, query.PropertyId, staysOf.Keys, cancellationToken))
        {
            var counted = staysOf.TryGetValue(property.Id, out var propertyStays)
                ? StayKpiRules.WithNightsIn(propertyStays, period.From, period.To).ToList()
                : [];
            rows.Add(new HostRevenueByPropertyRow(
                property.Id,
                property.Name,
                StayKpiRules.RevenueOf(counted, period.From, period.To),
                counted.Count,
                NightsIn(counted, period)));
        }

        // The total is the Home's: all the stays at once, rounded once on the sum.
        var all = StayKpiRules
            .WithNightsIn(staysOf.Values.SelectMany(s => s), period.From, period.To)
            .ToList();
        return new HostRevenueByProperty(
            period, rows, StayKpiRules.RevenueOf(all, period.From, period.To), all.Count, NightsIn(all, period));
    }

    public async Task<HostOccupancyByProperty> GetOccupancyByPropertyAsync(
        HostScope scope,
        HostReportQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var period = HostReportRules.PeriodOf(query.From, query.To);
        await EnsurePropertyInScopeAsync(scope, query.PropertyId, cancellationToken);

        var properties = await ActivePropertiesInScope(scope, query.PropertyId)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);
        var nights = await GetNightsByPropertyAsync(
            properties.Select(p => p.Id).ToList(), period, _clock.GetUtcNow().UtcDateTime, cancellationToken);

        var rows = properties
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .Select(p => new HostOccupancyByPropertyRow(p.Id, p.Name, nights[p.Id]))
            .ToList();
        return new HostOccupancyByProperty(period, rows, TotalOccupancy(nights.Values));
    }

    public async Task<HostBookingsByChannel> GetBookingsByChannelAsync(
        HostScope scope,
        HostReportQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var period = HostReportRules.PeriodOf(query.From, query.To);
        await EnsurePropertyInScopeAsync(scope, query.PropertyId, cancellationToken);

        var stays = await GetRevenueStaysAsync(scope, query.PropertyId, period, cancellationToken);
        var counted = StayKpiRules.WithNightsIn(stays, period.From, period.To).ToList();

        var rows = counted
            .GroupBy(s => s.Source)
            .Select(g => new HostBookingsByChannelRow(
                g.Key, g.Count(), NightsIn(g, period), StayKpiRules.RevenueOf(g, period.From, period.To)))
            .OrderByDescending(r => r.BookingCount)
            .ThenBy(r => r.Source)
            .ToList();
        return new HostBookingsByChannel(
            period, rows, counted.Count, NightsIn(counted, period), StayKpiRules.RevenueOf(counted, period.From, period.To));
    }

    // One 404 for a property that is missing, another org's or not reached by the caller: nothing tells them apart.
    private async Task EnsurePropertyInScopeAsync(HostScope scope, Guid? propertyId, CancellationToken cancellationToken)
    {
        if (propertyId is { } filtered && !await IsPropertyInScopeAsync(scope, filtered, cancellationToken))
            throw new NotFoundException($"Property {filtered} not found") { MessageKey = "PropertyNotFound" };
    }

    // The properties a revenue report lists: the active short-rent ones of the scope (a quiet one shows its zero) and any other one
    // of the scope that has revenue in the period (a paused or deleted one still has its guests and its money), by name.
    private async Task<IReadOnlyList<(Guid Id, string Name)>> ReportPropertiesAsync(
        HostScope scope,
        Guid? propertyId,
        IEnumerable<Guid> withRevenue,
        CancellationToken cancellationToken)
    {
        var properties = (await ActivePropertiesInScope(scope, propertyId)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken))
            .Select(p => (p.Id, p.Name))
            .ToList();
        var listed = properties.Select(p => p.Id).ToHashSet();
        var others = withRevenue.Where(id => !listed.Contains(id)).ToList();

        if (others.Count > 0)
        {
            var more = await db.Properties
                .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
                .AsNoTracking()
                .Where(p => p.OrgId == scope.OrgId && others.Contains(p.Id))
                .InScope(scope)
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(cancellationToken);
            properties.AddRange(more.Select(p => (p.Id, p.Name)));
        }

        return properties
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .ToList();
    }

    // The nights of the stays that fall in the period (the same nights the revenue is pro rata of).
    private static int NightsIn(IEnumerable<HostRevenueStay> stays, HostDashboardPeriod period) =>
        stays.Sum(s => PropertyOccupancy.NightsIn(s.CheckIn, s.CheckOut, period.From, period.To).Count());
}
