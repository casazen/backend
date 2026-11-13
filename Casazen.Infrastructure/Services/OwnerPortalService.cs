using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Owner portal data access service (ADR-011).
/// Filters by PropertyOwnership.OwnerUserId and active ownership period (ADR-011-R2, ADR-011-R8).
/// All responses mask guest data per ADR-011-R3: names NULL, emails NULL, phones NULL.
/// </summary>
/// <param name="dashboard">
/// The Home's numbers (NF-96): the occupancy of the property list and the overview and trend read them. The dependency injection always
/// passes it; it is optional only so that the places that build this service for the three older reads keep compiling.
/// </param>
public sealed class OwnerPortalService(AppDbContext db, TimeProvider timeProvider, IHostDashboardService? dashboard = null) : IOwnerPortalService
{
    /// <summary>
    /// Gets properties owned by the owner with occupancy and revenue summaries (ADR-011-R2, ADR-011-R8).
    /// Filters by ownership period: OwnershipStart &lt;= now AND (OwnershipEnd == null OR OwnershipEnd &gt;= now).
    /// Guest data is masked in booking summaries (ADR-011-R3).
    /// </summary>
    public async Task<IReadOnlyList<OwnerPortalPropertyInfo>> GetOwnerPropertiesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Get active property ownerships for this owner (ADR-011-R2, ADR-011-R8). The caller of the portal has no org of its own: the tenant filter of its
        // request is fail-closed and would match nothing, so every read here ignores it and names the owner, the org and the property explicitly (the soft
        // delete filter of the properties stays).
        var propertyIds = await Ownerships()
            .Where(po => po.OwnerUserId == ownerUserId
                      && po.OwnershipStart <= now
                      && (po.OwnershipEnd == null || po.OwnershipEnd >= now))
            .Select(po => po.PropertyId)
            .ToListAsync(cancellationToken);

        if (!propertyIds.Any())
            return [];

        // Get properties with their bookings. The subquery is built outside the projection: an expression tree cannot hold the list of the filters to ignore.
        var bookings = db.Set<Booking>().IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);
        var properties = await db.Set<Property>()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(p => propertyIds.Contains(p.Id))
            .Select(p => new OwnerPortalPropertyInfo
            {
                PropertyId = p.Id,
                PropertyName = p.Name,
                Address = p.Address,
                TotalBookings = bookings
                    .Count(b => b.OrgId == p.OrgId && b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled),
                TotalRevenue = bookings
                    .Where(b => b.OrgId == p.OrgId && b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled)
                    .Sum(b => (decimal?)b.TotalPrice) ?? 0m,
                RecentBookings = bookings
                    .Where(b => b.OrgId == p.OrgId && b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled)
                    .OrderByDescending(b => b.CheckInDate)
                    .Take(10)
                    .Select(b => new OwnerPortalBookingInfo
                    {
                        BookingId = b.Id,
                        CheckInDate = b.CheckInDate,
                        CheckOutDate = b.CheckOutDate,
                        Channel = b.Source.ToString(),
                        TotalAmount = b.TotalPrice,
                        Status = b.Status
                        // Guest data masked: no names, emails, phones per ADR-011-R3
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        // NF-96: the occupancy of the last 30 days is the Home's (the very nights the Home counts for «ultimi 30 giorni»), as a percentage
        // from 0 to 100 to one decimal, as the page prints it; it used to be a placeholder of 0.
        if (dashboard is not null)
        {
            var occupancy = await dashboard.GetLast30DaysOccupancyAsync(propertyIds, cancellationToken);
            foreach (var property in properties)
            {
                if (occupancy.TryGetValue(property.PropertyId, out var nights) && nights.Rate is { } rate)
                    property.OccupancyRate = (double)Math.Round(rate * 100m, 1, MidpointRounding.AwayFromZero);
            }
        }

        return properties;
    }

    /// <summary>
    /// Gets published monthly statements for the owner's properties (ADR-011-R2).
    /// Returns empty list for now (statements feature not yet implemented).
    /// </summary>
    public Task<IReadOnlyList<OwnerPortalStatementInfo>> GetOwnerStatementsAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        // Placeholder: statements feature not yet implemented
        return Task.FromResult<IReadOnlyList<OwnerPortalStatementInfo>>([]);
    }

    /// <summary>
    /// Gets invoices received by the owner (ADR-011-R2).
    /// Returns empty list for now (invoices feature not yet implemented).
    /// </summary>
    public Task<IReadOnlyList<OwnerPortalInvoiceInfo>> GetOwnerInvoicesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        // Placeholder: invoices feature not yet implemented
        return Task.FromResult<IReadOnlyList<OwnerPortalInvoiceInfo>>([]);
    }

    /// <summary>
    /// Gets withholding tax summary for the owner (ADR-011-R2).
    /// Returns empty summary for now (withholding feature not yet implemented).
    /// </summary>
    public Task<OwnerPortalWithholdingInfo> GetOwnerWithholdingAsync(
        Guid ownerUserId,
        int? year = null,
        CancellationToken cancellationToken = default)
    {
        var currentYear = year ?? timeProvider.GetUtcNow().Year;

        // Placeholder: withholding feature not yet implemented
        return Task.FromResult(new OwnerPortalWithholdingInfo
        {
            Year = currentYear,
            TotalWithheld = 0m,
            Details = []
        });
    }

    /// <summary>
    /// Verifies that the owner has access to the specified property (ADR-011-R2).
    /// Returns true if PropertyOwnership exists with active period, false otherwise.
    /// </summary>
    public async Task<bool> OwnerHasAccessToPropertyAsync(
        Guid ownerUserId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        return await Ownerships()
            .AnyAsync(po => po.OwnerUserId == ownerUserId
                         && po.PropertyId == propertyId
                         && po.OwnershipStart <= now
                         && (po.OwnershipEnd == null || po.OwnershipEnd >= now),
                      cancellationToken);
    }

    // ---------------------------------------------------------------- NF-96: the Home's numbers for the owner

    // A property of the owner and the part of it the owner holds (0..1): the weight of every figure of the Home for that property.
    private sealed record OwnedProperty(Guid OrgId, Guid PropertyId, decimal Weight);

    /// <summary>
    /// The performance of the owner's properties on the Home's numbers, weighted by the owner's shares (NF-96). The figures of each org
    /// come from <see cref="IHostDashboardService.GetPerformanceAsync"/> with that org's scope narrowed to the owner's properties; the
    /// total is the sum of the weighted rows. Guest data is never read.
    /// </summary>
    public async Task<OwnerPortalOverview> GetOwnerOverviewAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var host = HostDashboard();
        var owned = await OwnedPropertiesAsync(ownerUserId, query, cancellationToken);
        var period = HostReportRules.PeriodOf(query.From, query.To);
        var previousPeriod = query.CompareYear ? HostReportRules.PreviousYearPeriodOf(query.From, query.To) : null;

        var rows = new List<OwnerPortalRow>();
        foreach (var org in owned.GroupBy(o => o.OrgId))
        {
            var weights = org.ToDictionary(o => o.PropertyId, o => o.Weight);
            var report = await host.GetPerformanceAsync(
                new HostScope(org.Key),
                new HostPerformanceQuery(
                    query.From, query.To, null, HostPerformanceGroupBy.Property, query.CompareYear, weights.Keys.ToList()),
                cancellationToken);

            foreach (var row in report.Rows)
            {
                if (row.PropertyId is not { } propertyId || !weights.TryGetValue(propertyId, out var weight))
                    continue;

                rows.Add(new OwnerPortalRow(
                    row.Key,
                    propertyId,
                    row.Label,
                    OwnerPerformanceFigures.Of(row.Figures, weight),
                    row.PreviousYear is null ? null : OwnerPerformanceFigures.Of(row.PreviousYear, weight)));
            }
        }

        rows = rows
            .OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        var previous = rows.Where(r => r.PreviousYear is not null).Select(r => r.PreviousYear!).ToList();
        var total = new OwnerPortalRow(
            HostPerformanceRules.TotalKey,
            null,
            HostPerformanceRules.TotalKey,
            rows.Aggregate(OwnerPerformanceFigures.Zero, (sum, row) => sum.Plus(row.Figures)),
            previous.Count == 0 ? null : previous.Aggregate(OwnerPerformanceFigures.Zero, (sum, figures) => sum.Plus(figures)));

        return new OwnerPortalOverview(
            period,
            previousPeriod,
            query.GroupBy == HostPerformanceGroupBy.Property ? rows : [],
            total);
    }

    /// <summary>
    /// The owner's trend (NF-96): the Home's trend of each org, narrowed to the owner's properties, each property line weighted by the
    /// owner's share. The buckets are the host's (<see cref="HostReportRules.TrendBucketsOf"/>); all the properties together are the sum of
    /// the weighted lines.
    /// </summary>
    public async Task<OwnerPortalTrend> GetOwnerTrendAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var host = HostDashboard();
        var owned = await OwnedPropertiesAsync(ownerUserId, query, cancellationToken);
        var period = HostReportRules.PeriodOf(query.From, query.To);
        var previousPeriod = query.CompareYear ? HostReportRules.PreviousYearPeriodOf(query.From, query.To) : null;
        var (unit, buckets) = HostReportRules.TrendBucketsOf(period, HostTrendBucketUnit.Auto, query.CompareYear);

        // One weighted line for each property of the owner, in every org: the host's trend cut by property, none of it folded.
        var lines = new List<OwnerPortalSeries>();
        foreach (var org in owned.GroupBy(o => o.OrgId))
        {
            var weights = org.ToDictionary(o => o.PropertyId, o => o.Weight);
            var trend = await host.GetTrendAsync(
                new HostScope(org.Key),
                new HostTrendQuery(
                    query.From,
                    query.To,
                    null,
                    HostPerformanceGroupBy.Property,
                    query.CompareYear,
                    weights.Keys.ToList(),
                    HostTrendBucketUnit.Auto,
                    int.MaxValue),
                cancellationToken);

            foreach (var line in trend.Series)
            {
                if (line.PropertyId is not { } propertyId || !weights.TryGetValue(propertyId, out var weight))
                    continue;

                lines.Add(new OwnerPortalSeries(
                    line.Key,
                    line.Label,
                    propertyId,
                    line.Points.Select(p => OwnerPerformanceFigures.Of(p.Figures, weight)).ToList(),
                    line.PreviousYear.Select(p => p is null ? null : OwnerPerformanceFigures.Of(p.Figures, weight)).ToList()));
            }
        }

        var series = query.GroupBy == HostPerformanceGroupBy.Property
            ? FoldLines(lines)
            : [Together(HostPerformanceRules.TotalKey, lines, buckets.Count, query.CompareYear)];
        return new OwnerPortalTrend(period, previousPeriod, unit, buckets, series);
    }

    // The ownerships of the owner that count for the days of the query, one entry for each property with the part held. An owner sees
    // only its own ownerships (ADR-011-R2): the ones in force today that had started by the last day and had not ended before the first.
    private async Task<List<OwnedProperty>> OwnedPropertiesAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ownerships = await Ownerships()
            .Where(po => po.OwnerUserId == ownerUserId
                         && po.OwnershipStart <= now
                         && (po.OwnershipEnd == null || po.OwnershipEnd >= now))
            .Select(po => new { po.OrgId, po.PropertyId, po.SharePercent, po.OwnershipStart, po.OwnershipEnd })
            .ToListAsync(cancellationToken);

        // Several rows of one property are parts of one whole; the owner never holds more than all of it.
        var owned = ownerships
            .Where(po => RomeCalendar.DateInRome(po.OwnershipStart) <= query.To
                         && (po.OwnershipEnd is not { } end || RomeCalendar.DateInRome(end) >= query.From))
            .GroupBy(po => (po.OrgId, po.PropertyId))
            .Select(group => new OwnedProperty(group.Key.OrgId, group.Key.PropertyId, Math.Min(1m, group.Sum(po => po.SharePercent) / 100m)))
            .Where(o => o.Weight > 0m)
            .ToList();

        if (query.PropertyId is not { } asked)
            return owned;

        // One 404 for a property that is missing, another owner's, or no longer (or not yet) the owner's.
        owned = owned.Where(o => o.PropertyId == asked).ToList();
        if (owned.Count == 0)
            throw new NotFoundException($"Property {asked} not found") { MessageKey = "PropertyNotFound" };

        return owned;
    }

    /// <summary>
    /// The ownerships of every org: the caller of the owner portal has no org of its own (the tenant filter of its request is fail-closed and would answer
    /// nothing), so what an owner reaches is named by its own <c>OwnerUserId</c> in each predicate.
    /// </summary>
    private IQueryable<PropertyOwnership> Ownerships() =>
        db.Set<PropertyOwnership>().IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    private IHostDashboardService HostDashboard() =>
        dashboard ?? throw new InvalidOperationException("The owner portal numbers need the Home's service, which the dependency injection provides.");

    // The lines of the properties, the ones with most revenue first; from MaxTrendSeries + 1 on, the 25th line is the sum of the rest.
    private static List<OwnerPortalSeries> FoldLines(List<OwnerPortalSeries> lines)
    {
        var ordered = lines
            .OrderByDescending(line => line.Points.Sum(p => p.Revenue))
            .ThenBy(line => line.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.Key, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count <= HostPerformanceRules.MaxTrendSeries)
            return ordered;

        var kept = ordered.Take(HostPerformanceRules.MaxTrendSeries - 1).ToList();
        var rest = ordered.Skip(HostPerformanceRules.MaxTrendSeries - 1).ToList();
        kept.Add(Together(HostPerformanceRules.OtherSeriesKey, rest, rest[0].Points.Count, rest[0].PreviousYear.Count > 0));
        return kept;
    }

    // Several lines as one: the points added bucket by bucket; a bucket of the year before is empty only when it is for every line.
    private static OwnerPortalSeries Together(string key, IReadOnlyList<OwnerPortalSeries> lines, int buckets, bool compared)
    {
        var points = new List<OwnerPerformanceFigures>(buckets);
        var before = new List<OwnerPerformanceFigures?>();
        for (var index = 0; index < buckets; index++)
        {
            points.Add(lines.Aggregate(OwnerPerformanceFigures.Zero, (sum, line) => sum.Plus(line.Points[index])));
            if (!compared)
                continue;

            var kept = lines.Select(line => line.PreviousYear[index]).Where(p => p is not null).Select(p => p!).ToList();
            before.Add(kept.Count == 0 ? null : kept.Aggregate(OwnerPerformanceFigures.Zero, (sum, figures) => sum.Plus(figures)));
        }

        return new OwnerPortalSeries(key, key, null, points, before);
    }
}
