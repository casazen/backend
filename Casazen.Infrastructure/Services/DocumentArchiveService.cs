using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IDocumentArchiveService" />
/// <remarks>
/// <para><b>One statement for the list, whatever the number of sources.</b> Each source of an allowed type gives its query already narrowed to the
/// org and the reach of the caller (<see cref="IDocumentArchiveSource.Query"/>); the filters of the request are applied to each of them, and the
/// queries are concatenated (<c>UNION ALL</c>) with the same member-initialiser, then counted, ordered and paged by the database. A source that
/// is not allowed, or not asked for, is not in the statement at all, so a type the caller may not read is never even read.</para>
/// </remarks>
public sealed class DocumentArchiveService(
    IEnumerable<IDocumentArchiveSource> sources,
    AppDbContext db) : IDocumentArchiveService
{
    private readonly IReadOnlyList<IDocumentArchiveSource> _sources = [.. sources];
    private readonly bool _postgres = db.Database.IsNpgsql();

    public async Task<DocumentArchivePage> ListAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Clamp(query.Page, 1, MaxPage);
        var pageSize = Math.Clamp(query.PageSize, 1, DocumentArchiveQuery.MaxPageSize);
        var rows = AllowedRows(scope, access, query, byType: true);
        if (rows is null)
            return new DocumentArchivePage([], 0, page, pageSize);

        var total = await rows.CountAsync(cancellationToken);
        if (total == 0)
            return new DocumentArchivePage([], 0, page, pageSize);

        var items = await rows
            .OrderByDescending(r => r.DocumentDate)
            .ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new DocumentArchivePage([.. items.Select(WithContentType)], total, page, pageSize);
    }

    public async Task<IReadOnlyList<ArchiveTypeCount>> CountByTypeAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(query);

        // The type filter is the one thing a count by type leaves out: the chips show what choosing each type would give.
        var rows = AllowedRows(scope, access, query, byType: false);
        var counts = new Dictionary<ArchiveDocumentType, int>();
        if (rows is not null)
        {
            var grouped = await rows.GroupBy(r => r.Type).Select(g => new { Type = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
            foreach (var group in grouped)
                counts[group.Type] = group.Count;
        }

        return
        [
            .. _sources
                .Select(s => s.Type)
                .Where(access.Allows)
                .Distinct()
                .Order()
                .Select(type => new ArchiveTypeCount(type, counts.GetValueOrDefault(type))),
        ];
    }

    /// <summary>The last page a caller can ask for: with the largest page size it still fits in an <c>int</c> offset.</summary>
    private const int MaxPage = 10_000_000;

    /// <summary>
    /// The queries of the sources the caller may read (and, with <paramref name="byType"/>, that the request asks for), filtered by the request
    /// and concatenated; <c>null</c> when no source qualifies. An empty list of types selects nothing, never everything.
    /// </summary>
    private IQueryable<ArchiveRow>? AllowedRows(HostScope scope, DocumentArchiveAccess access, DocumentArchiveQuery query, bool byType)
    {
        IQueryable<ArchiveRow>? union = null;
        foreach (var source in _sources)
        {
            if (!access.Allows(source.Type) || (byType && query.Types is { } asked && !asked.Contains(source.Type)))
                continue;

            var rows = Filtered(source.Query(scope), query);
            union = union is null ? rows : union.Concat(rows);
        }

        return union;
    }

    /// <summary>The filters of the request on one source: days, property, booking and the search. Applied to each source, before they are concatenated.</summary>
    private IQueryable<ArchiveRow> Filtered(IQueryable<ArchiveRow> rows, DocumentArchiveQuery query)
    {
        if (query.From is { } from)
            rows = rows.Where(r => r.DocumentDate >= from);

        if (query.To is { } to)
            rows = rows.Where(r => r.DocumentDate <= to);

        if (query.PropertyId is { } propertyId)
            rows = rows.Where(r => r.PropertyId == propertyId);

        if (query.BookingId is { } bookingId)
            rows = rows.Where(r => r.BookingId == bookingId);

        var text = query.Search?.Trim();
        if (string.IsNullOrEmpty(text))
            return rows;

        if (text.Length > DocumentArchiveQuery.MaxSearchLength)
            text = text[..DocumentArchiveQuery.MaxSearchLength];

        if (_postgres)
        {
            // ILIKE with the wildcards of the text escaped: a search for 50% or a_b finds those characters, not everything.
            var pattern = SupplierAdminService.LikePattern(text)!;
            var escape = SupplierAdminService.LikeEscape;
            return rows.Where(r => EF.Functions.ILike(r.Title, pattern, escape)
                || (r.PropertyName != null && EF.Functions.ILike(r.PropertyName, pattern, escape))
                || (r.BookingCode != null && EF.Functions.ILike(r.BookingCode, pattern, escape)));
        }

        var lower = text.ToLowerInvariant();
        return rows.Where(r => r.Title.ToLower().Contains(lower)
            || (r.PropertyName != null && r.PropertyName.ToLower().Contains(lower))
            || (r.BookingCode != null && r.BookingCode.ToLower().Contains(lower)));
    }

    /// <summary>A row whose source could not say the MIME type in SQL gets it from the extension of its title.</summary>
    private static ArchiveRow WithContentType(ArchiveRow row) =>
        row.ContentType.Length > 0 ? row : row with { ContentType = StorageKeys.ContentTypeFor(row.Title) };
}
