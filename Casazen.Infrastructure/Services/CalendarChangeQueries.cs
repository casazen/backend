using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="ICalendarChangeQueries"/> (E08/F05/US02/T02, NF-36): the changes of the calendar sent to the channel manager, as the host reads them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Org and reach.</b> Every query names the org of the <see cref="HostScope"/> (on top of the tenant filter of the context). A process is shown only when the
/// caller reaches <b>every</b> property it touches (<c>InScope</c>, in SQL, the way every list of the host is narrowed): a change that also touches a property the
/// caller does not reach would show the state and the dates of that property. A process whose properties cannot be told (no id in its summary and no chunk) is
/// shown to an org-wide scope only, never to a restricted one: no declaration is never «in scope».</item>
/// <item><b>The properties of a process</b> are the ones of its summary (written by the recorder) and the ones of its chunks, so a change that stays in CasaZen
/// (a property that is not on the channel manager has no chunk) is read as such: <c>Skipped</c>.</item>
/// <item>No personal data: ids, dates, states, counts and stable codes.</item>
/// </list>
/// </remarks>
public sealed class CalendarChangeQueries(AppDbContext db) : ICalendarChangeQueries
{
    /// <summary>How many processes a page of candidates holds when the caller reaches only some properties.</summary>
    private const int PageSize = 100;

    /// <summary>How many pages of candidates are read at most for a caller limited to some properties.</summary>
    private const int MaxPages = 5;

    private sealed record ChunkRow(Guid ProcessId, Guid PropertyId, CalendarChunkState State, string? ErrorCode, DateOnly From, DateOnly To);

    public async Task<IReadOnlyList<CalendarChangeView>> ListAsync(
        HostScope scope,
        CalendarChangeListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var limit = query.EffectiveLimit;
        var processes = db.CalendarChangeProcesses.AsNoTracking().Where(p => p.OrgId == scope.OrgId);
        if (query.State is { } state)
            processes = processes.Where(p => p.State == state);

        var ordered = processes.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id);

        // The whole org reads one page of the size asked for; a caller limited to some properties reads pages of candidates until it has enough it may see.
        var size = scope.IsOrgWide ? limit : PageSize;
        var pages = scope.IsOrgWide ? 1 : MaxPages;
        var result = new List<CalendarChangeView>();
        for (var page = 0; page < pages && result.Count < limit; page++)
        {
            var rows = await ordered.Skip(page * size).Take(size).ToListAsync(cancellationToken);
            if (rows.Count == 0)
                break;

            result.AddRange(await VisibleViewsAsync(scope, rows, cancellationToken));
            if (rows.Count < size)
                break;
        }

        return result.Take(limit).ToList();
    }

    public async Task<CalendarChangeView?> GetAsync(HostScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var row = await db.CalendarChangeProcesses
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrgId == scope.OrgId && p.Id == id, cancellationToken);
        if (row is null)
            return null;

        return (await VisibleViewsAsync(scope, [row], cancellationToken)).FirstOrDefault();
    }

    /// <summary>The views of the processes of <paramref name="rows"/> the scope reaches, in the order of the rows.</summary>
    private async Task<List<CalendarChangeView>> VisibleViewsAsync(HostScope scope, IReadOnlyList<CalendarChangeProcess> rows, CancellationToken ct)
    {
        var processIds = rows.Select(r => r.Id).ToList();
        var chunks = await db.CalendarChangeChunks
            .AsNoTracking()
            .Where(c => c.OrgId == scope.OrgId && processIds.Contains(c.ProcessId))
            .Select(c => new ChunkRow(c.ProcessId, c.PropertyId, c.State, c.ErrorCode, c.FromDate, c.ToDate))
            .ToListAsync(ct);
        var chunksOf = chunks.ToLookup(c => c.ProcessId);
        var propertiesOf = rows.ToDictionary(
            r => r.Id,
            r => CalendarChangeSummary.PropertyIdsOf(r.SummaryJson).Union(chunksOf[r.Id].Select(c => c.PropertyId)).ToList());

        // The properties the scope reaches, in SQL: the same narrowing as every list of the host.
        HashSet<Guid>? reachable = null;
        if (!scope.IsOrgWide)
        {
            var all = propertiesOf.Values.SelectMany(ids => ids).Distinct().ToList();
            reachable = (await db.Properties
                    .AsNoTracking()
                    .Where(p => p.OrgId == scope.OrgId && all.Contains(p.Id))
                    .InScope(scope)
                    .Select(p => p.Id)
                    .ToListAsync(ct))
                .ToHashSet();
        }

        var views = new List<CalendarChangeView>();
        foreach (var row in rows)
        {
            var propertyIds = propertiesOf[row.Id];
            if (reachable is not null && (propertyIds.Count == 0 || !propertyIds.All(reachable.Contains)))
                continue;

            views.Add(ViewOf(row, propertyIds, chunksOf[row.Id].ToList()));
        }

        return views;
    }

    private static CalendarChangeView ViewOf(CalendarChangeProcess row, IReadOnlyList<Guid> propertyIds, IReadOnlyList<ChunkRow> chunks)
    {
        // The dates the summary names, else the ones of the chunks.
        var dates = CalendarChangeSummary.DatesOf(row.SummaryJson);
        DateOnly? first = dates?.From;
        DateOnly? last = dates?.To;
        if (chunks.Count > 0)
        {
            first ??= chunks.Min(c => c.From);
            last ??= chunks.Max(c => c.To);
        }

        return new CalendarChangeView(
            row.Id,
            row.Kind,
            row.State,
            row.CreatedAt,
            row.StartedAt,
            row.FinishedAt,
            row.PropertyCount,
            row.DayCount,
            row.QuotaUnitsEstimated,
            first,
            last,
            row.ErrorCode,
            row.NextAttemptAt,
            propertyIds.Select(id => PropertyViewOf(id, chunks.Where(c => c.PropertyId == id).ToList())).ToList());
    }

    /// <summary>
    /// One property of a change from its chunks: failed when one failed (with the code of the first, by date), else pending, else accepted, else confirmed; a property
    /// with no chunk, or only skipped ones, sent nothing.
    /// </summary>
    private static CalendarChangePropertyView PropertyViewOf(Guid propertyId, IReadOnlyList<ChunkRow> chunks)
    {
        if (chunks.Count == 0)
            return new CalendarChangePropertyView(propertyId, CalendarChangePropertyState.Skipped, null, 0, null, null);

        var failed = chunks.Where(c => c.State == CalendarChunkState.Failed).OrderBy(c => c.From).FirstOrDefault();
        var state = failed is not null
            ? CalendarChangePropertyState.Failed
            : chunks.Any(c => c.State == CalendarChunkState.Pending)
                ? CalendarChangePropertyState.Pending
                : chunks.Any(c => c.State == CalendarChunkState.Accepted)
                    ? CalendarChangePropertyState.Accepted
                    : chunks.Any(c => c.State == CalendarChunkState.Confirmed)
                        ? CalendarChangePropertyState.Confirmed
                        : CalendarChangePropertyState.Skipped;
        return new CalendarChangePropertyView(
            propertyId,
            state,
            failed?.ErrorCode,
            chunks.Count,
            chunks.Min(c => c.From),
            chunks.Max(c => c.To));
    }
}
