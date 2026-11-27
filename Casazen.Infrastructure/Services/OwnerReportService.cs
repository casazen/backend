using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The report by property owner of an agency (NF-96). The figures of every property are read once, through
/// <see cref="IHostDashboardService.GetPerformanceAsync"/> (so they are the Home's, never a second rule), and split by owner in memory:
/// each owner counts the part it holds of each property in the days of the report, the properties nobody owns go to the unassigned row.
/// </summary>
/// <remarks>
/// <para><b>What an owner holds.</b> The ownerships of the org (<c>PropertyOwnership</c>, with their history) whose period overlaps the
/// days of the report. The weight of an ownership on a property is its share (<c>SharePercent</c> / 100) times the part of the days of
/// the report it was in force for, so an owner who sold the house on 15 March counts it for the days it had it, and the buyer for the
/// rest: the weights of a property never add up to more than one (they are scaled down if the data say otherwise), and what no owner
/// holds goes to the unassigned row. A co-owned house counts once in the grand total.</para>
/// <para><b>Privacy.</b> Only the id and the name of an owner are read: never the fiscal code, the IBAN, the e-mail or the phone, which
/// are not even selected (and so not decrypted).</para>
/// </remarks>
public sealed class OwnerReportService(AppDbContext db, IHostDashboardService dashboard) : IOwnerReportService
{
    // The weights below are decimals: a sum of thirds is short of one by a few billionths, which is not a property nobody owns.
    private const decimal Tolerance = 0.000001m;

    private sealed record Part(Guid OwnerId, Guid PropertyId, decimal Weight);

    private sealed record OwnerName(Guid Id, string FirstName, string LastName, bool Anonymized);

    public async Task<OwnerPerformanceReport> GetByOwnerAsync(
        HostScope scope,
        HostPerformanceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        // The Home's figures of every property of the scope, once; the same call answers the 404 of a property the caller does not reach.
        var performance = await dashboard.GetPerformanceAsync(
            scope, query with { GroupBy = HostPerformanceGroupBy.Property, CompareYear = false }, cancellationToken);
        var period = performance.Period;
        var figuresOf = performance.Rows
            .Where(r => r.PropertyId is not null)
            .ToDictionary(r => r.PropertyId!.Value, r => r.Figures);

        var parts = await PartsAsync(scope.OrgId, figuresOf.Keys.ToList(), query, period.Nights, cancellationToken);

        // Only the id and the name of an owner are read: the fiscal code, the IBAN, the e-mail and the phone are not even selected.
        var ownerIds = parts.Select(p => p.OwnerId).Distinct().ToList();
        List<OwnerName> owners = [];
        if (ownerIds.Count > 0)
        {
            owners = await db.PropertyOwners
                .AsNoTracking()
                .Where(o => o.OrgId == scope.OrgId && ownerIds.Contains(o.Id))
                .Select(o => new OwnerName(o.Id, o.FirstName, o.LastName, o.AnonymizedAt != null))
                .ToListAsync(cancellationToken);
        }

        // An owner that is not in the registry any more (it cannot happen: the link cascades) would leave its part nowhere: it stays unassigned.
        var known = owners.Select(o => o.Id).ToHashSet();
        parts = parts.Where(p => known.Contains(p.OwnerId)).ToList();

        var rows = owners
            .Select(owner =>
            {
                var mine = parts.Where(p => p.OwnerId == owner.Id).ToList();
                var figures = mine.Aggregate(
                    OwnerPerformanceFigures.Zero,
                    (sum, part) => sum.Plus(OwnerPerformanceFigures.Of(figuresOf[part.PropertyId], part.Weight)));
                var name = owner.Anonymized ? null : $"{owner.FirstName} {owner.LastName}".Trim();
                return new OwnerPerformanceRow(owner.Id, name, owner.Anonymized, mine.Select(p => p.PropertyId).Distinct().Count(), figures);
            })
            .OrderBy(r => r.IsAnonymized)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.OwnerId)
            .ToList();

        // What no owner holds of a property is the unassigned part: owners and this row add up to the Home's total.
        var held = parts.GroupBy(p => p.PropertyId).ToDictionary(g => g.Key, g => g.Sum(p => p.Weight));
        var unassigned = OwnerPerformanceFigures.Zero;
        var unassignedCount = 0;
        foreach (var (propertyId, figures) in figuresOf)
        {
            var rest = 1m - (held.TryGetValue(propertyId, out var share) ? share : 0m);
            if (rest < Tolerance)
                continue;

            unassigned = unassigned.Plus(OwnerPerformanceFigures.Of(figures, rest));
            unassignedCount++;
        }

        return new OwnerPerformanceReport(
            period,
            rows,
            new OwnerPerformanceRow(null, null, false, unassignedCount, unassigned),
            OwnerPerformanceFigures.Of(performance.Total.Figures));
    }

    // The part each owner holds of each property of the report, in the days of the report.
    private async Task<List<Part>> PartsAsync(
        Guid orgId,
        List<Guid> propertyIds,
        HostPerformanceQuery query,
        int periodDays,
        CancellationToken cancellationToken)
    {
        if (propertyIds.Count == 0)
            return [];

        // Generous in SQL (the instants of the Rome days), exact below (the days).
        var startsAt = RomeCalendar.StartOfDayUtc(query.From.ToDateTime(TimeOnly.MinValue));
        var endsAt = RomeCalendar.StartOfDayUtc(query.To.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var ownerships = await db.PropertyOwnerships
            .AsNoTracking()
            .Where(po => po.OrgId == orgId
                         && propertyIds.Contains(po.PropertyId)
                         && po.OwnershipStart < endsAt
                         && (po.OwnershipEnd == null || po.OwnershipEnd >= startsAt))
            .Select(po => new { po.PropertyOwnerId, po.PropertyId, po.SharePercent, po.OwnershipStart, po.OwnershipEnd })
            .ToListAsync(cancellationToken);

        var parts = new List<Part>();
        foreach (var house in ownerships.GroupBy(o => o.PropertyId))
        {
            var weights = house
                .Select(o => (Owner: o.PropertyOwnerId, Weight: o.SharePercent / 100m * OverlapDays(o.OwnershipStart, o.OwnershipEnd, query) / periodDays))
                .Where(w => w.Weight > 0m)
                .ToList();

            // A property has one whole to give: if the data say more, everyone gets a proportional part of it.
            var sum = weights.Sum(w => w.Weight);
            var scale = sum > 1m ? 1m / sum : 1m;
            foreach (var owner in weights.GroupBy(w => w.Owner))
                parts.Add(new Part(owner.Key, house.Key, owner.Sum(w => w.Weight) * scale));
        }

        return parts;
    }

    // The days of the report (Rome calendar days, both included) the ownership was in force: from the day it started to the day it ended, included.
    private static int OverlapDays(DateTime start, DateTime? end, HostPerformanceQuery query)
    {
        var first = RomeCalendar.DateInRome(start);
        var last = end is { } ended ? RomeCalendar.DateInRome(ended) : DateOnly.MaxValue;
        var from = first > query.From ? first : query.From;
        var to = last < query.To ? last : query.To;
        return to < from ? 0 : to.DayNumber - from.DayNumber + 1;
    }
}
