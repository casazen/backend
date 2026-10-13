using System.Runtime.CompilerServices;
using Casazen.Core.Entities;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgActivityService" />
/// <remarks>
/// <para>The global tenant filter stays on, next to the explicit <c>OrgId</c> of every query: the org passed in is the caller's own,
/// and if the request's tenant were ever another, the list would be empty rather than someone else's. (The services that
/// write across orgs, like the acceptance of an invitation, have to ignore the filter; this one only reads the caller's.)</para>
/// <para>Nothing is added to what the lines hold: ids, codes and the instant. The order is the newest first, and by id among
/// lines of the same instant, so two pages never repeat or skip a line while nothing is written.</para>
/// </remarks>
public sealed class OrgActivityService(AppDbContext db) : IOrgActivityService
{
    /// <summary>The page the arithmetic of the offset cannot overflow past: far beyond any real log, answered as an empty page.</summary>
    private const int LastPage = int.MaxValue / OrgActivityRules.MaxPageSize;

    public async Task<OrgActivityPage> ListAsync(
        Guid orgId,
        OrgActivityFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        page = Math.Clamp(page, 1, LastPage);
        pageSize = Math.Clamp(pageSize, 1, OrgActivityRules.MaxPageSize);

        var query = Query(orgId, filter);
        var total = await query.CountAsync(cancellationToken);
        var rows = await Ordered(query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new OrgActivityPage(rows.Select(ToItem).ToList(), total, page, pageSize);
    }

    public async IAsyncEnumerable<OrgActivityItem> StreamAsync(
        Guid orgId,
        OrgActivityFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await foreach (var row in Ordered(Query(orgId, filter)).AsAsyncEnumerable().WithCancellation(cancellationToken))
            yield return ToItem(row);
    }

    private IQueryable<OrgActivityEntry> Query(Guid orgId, OrgActivityFilter filter)
    {
        var query = db.OrgActivityEntries.AsNoTracking().Where(e => e.OrgId == orgId);

        if (filter.From is { } from)
            query = query.Where(e => e.When >= from);
        if (filter.To is { } to)
            query = query.Where(e => e.When <= to);

        if (filter.Types is { Count: > 0 } types)
        {
            var wanted = types.Distinct().ToList();
            query = query.Where(e => wanted.Contains(e.Type));
        }

        if (filter.Area is { } area)
            query = query.Where(e => e.Area == area);

        if (filter.SystemActor)
        {
            query = query.Where(e => e.ActorUserId == null);
        }
        else if (!string.IsNullOrWhiteSpace(filter.ActorUserId))
        {
            var actor = filter.ActorUserId.Trim();
            query = query.Where(e => e.ActorUserId == actor);
        }

        return query;
    }

    private static IOrderedQueryable<OrgActivityEntry> Ordered(IQueryable<OrgActivityEntry> query) =>
        query.OrderByDescending(e => e.When).ThenByDescending(e => e.Id);

    private static OrgActivityItem ToItem(OrgActivityEntry entry) => new(
        entry.Id,
        entry.When,
        entry.ActorUserId,
        entry.Area,
        entry.Type,
        entry.SubjectType,
        entry.SubjectId,
        OrgActivityDetails.Parse(entry.DetailsJson));
}
