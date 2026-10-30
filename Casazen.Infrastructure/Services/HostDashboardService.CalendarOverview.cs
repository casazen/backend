using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>The calendars of every property of the host in one read (SR-10b).</summary>
/// <remarks>
/// It adds no rule of its own: the state of a feed is <see cref="GetIcalFeedsAsync"/> (the dashboard's read, which never selects the
/// import URL), and what this adds is the properties without a feed and the token of the export link of each property. Three queries
/// whatever the number of properties (the properties, the feeds, the links), all limited to the scope in SQL. Nothing is created: a
/// property whose link nobody asked for has no token yet. The token is a secret in practice (the link shows the dates taken to who has
/// it): it is returned to the web layer and written nowhere here.
/// </remarks>
public sealed partial class HostDashboardService
{
    public async Task<HostCalendarOverview> GetCalendarOverviewAsync(
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // The properties the host lists (active, short rent, in reach), the same set the Home counts.
        var properties = await ActivePropertiesInScope(scope, null)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);
        var feedsOf = (await GetIcalFeedsAsync(scope, cancellationToken)).ToLookup(feed => feed.PropertyId);

        // The links of the properties in reach and of no others: a token read for a property the caller does not reach would be a leak.
        var tokens = await db.PropertyICalExports
            .AsNoTracking()
            .Where(export => export.OrgId == scope.OrgId)
            .InScope(scope, export => export.Property)
            .Select(export => new { export.PropertyId, export.ExportToken })
            .ToDictionaryAsync(export => export.PropertyId, export => export.ExportToken, cancellationToken);

        return new HostCalendarOverview(properties
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .Select(p => new HostCalendarOverviewProperty(
                p.Id,
                p.Name,
                tokens.TryGetValue(p.Id, out var token) ? token : null,
                feedsOf[p.Id].ToList()))
            .ToList());
    }
}
