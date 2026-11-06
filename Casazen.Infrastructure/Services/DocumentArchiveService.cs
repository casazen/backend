using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IDocumentArchiveService" />
/// <remarks>
/// <para><b>One statement for the list, whatever the number of sources.</b> Each source of an allowed type gives its query already narrowed to the
/// org and the reach of the caller (<see cref="IDocumentArchiveSource.Query"/>); the filters of the request are applied to each of them, and the
/// queries are concatenated (<c>UNION ALL</c>) with the same member-initialiser, then counted, ordered and paged by the database. A source that
/// is not allowed, or not asked for, is not in the statement at all, so a type the caller may not read is never even read.</para>
/// <para><b>The same documents everywhere.</b> The download looks the row up with the very query of the list, and the export lists its
/// documents with it: what is not in the list for a caller cannot be downloaded or exported by it. The org of the scope is in every query.</para>
/// <para><b>Nothing personal in the log.</b> Titles, property names and the search text are never logged; a missing file is logged by its ids.</para>
/// </remarks>
public sealed class DocumentArchiveService(
    IEnumerable<IDocumentArchiveSource> sources,
    AppDbContext db,
    IOptions<DocumentArchiveOptions> options,
    ILogger<DocumentArchiveService> logger) : IDocumentArchiveService
{
    /// <summary>Code of the 403 of a type the caller may not read.</summary>
    public const string TypeForbiddenCode = "archive_type_forbidden";

    /// <summary>Code of the 404 of a document that is missing, of another org or outside the scope of the caller.</summary>
    public const string DocumentNotFoundCode = "archive_document_not_found";

    /// <summary>Code of the 404 of a document that has no file (or whose file is gone from the storage).</summary>
    public const string DocumentHasNoFileCode = "archive_document_has_no_file";

    /// <summary>Code of the 422 of an export that holds more documents or bytes than the caps allow.</summary>
    public const string ExportTooLargeCode = "archive_export_too_large";

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

    public async Task<ArchiveFile> OpenAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        ArchiveDocumentType type,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(access);

        if (!access.Allows(type))
            throw new DomainForbiddenException(TypeForbiddenCode, "ArchiveTypeForbidden");

        // The row is looked up with the query of the list (org, scope), so another org's document, one outside the reach of the caller and one
        // that does not exist are the same 404.
        var candidates = _sources.Where(s => s.Type == type).ToList();
        var inReach = false;
        foreach (var source in candidates)
        {
            if (await source.Query(scope).AnyAsync(r => r.Id == id, cancellationToken))
            {
                inReach = true;
                break;
            }
        }

        if (!inReach)
            throw new NotFoundException("Archive document not found.") { Code = DocumentNotFoundCode, MessageKey = "ArchiveDocumentNotFound" };

        return await OpenFileAsync(type, id, scope, cancellationToken)
            ?? throw new NotFoundException("Archive document has no file.") { Code = DocumentHasNoFileCode, MessageKey = "ArchiveDocumentHasNoFile" };
    }

    public async Task<DocumentArchiveExportResult> ExportAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery query,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(destination);

        var limits = options.Value;
        var rows = AllowedRows(scope, access, query, byType: true);

        // The caps are checked before a byte is written: a refusal is a 422 the host can act on, never a download that stops half way.
        var found = rows is null ? 0 : await rows.CountAsync(cancellationToken);
        // A document that stores no size (every property document) counts as the largest upload, never as zero: the cap bounds the response.
        var declaredBytes = rows is null || found == 0
            ? 0L
            : await rows.SumAsync(r => r.SizeBytes ?? DocumentArchiveOptions.UndeclaredDocumentBytes, cancellationToken);
        if (found > limits.MaxExportFiles || declaredBytes > limits.MaxExportBytes)
            throw TooLarge(found, limits);

        List<ArchiveRow> documents = rows is null || found == 0
            ? []
            : await rows
                .OrderBy(r => r.Type)
                .ThenBy(r => r.DocumentDate)
                .ThenBy(r => r.Id)
                .Take(limits.MaxExportFiles + 1)
                .ToListAsync(cancellationToken);
        if (documents.Count > limits.MaxExportFiles)
            throw TooLarge(documents.Count, limits);

        return await DocumentArchiveExport.WriteAsync(
            documents,
            async (row, token) =>
            {
                var file = await OpenFileAsync(row.Type, row.Id, scope, token);
                if (file is null)
                    logger.LogWarning("The file of an archive document is missing. OrgId={OrgId} Type={Type} DocumentId={DocumentId}", scope.OrgId, row.Type, row.Id);

                return file;
            },
            destination,
            cancellationToken);
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

    /// <summary>The first file a source of <paramref name="type"/> opens for the row: more than one source may share a type.</summary>
    private async Task<ArchiveFile?> OpenFileAsync(ArchiveDocumentType type, Guid id, HostScope scope, CancellationToken cancellationToken)
    {
        foreach (var source in _sources.Where(s => s.Type == type))
        {
            var file = await source.OpenAsync(id, scope, cancellationToken);
            if (file is not null)
                return file;
        }

        return null;
    }

    /// <summary>A row whose source could not say the MIME type in SQL gets it from the extension of its title.</summary>
    private static ArchiveRow WithContentType(ArchiveRow row) =>
        row.ContentType.Length > 0 ? row : row with { ContentType = StorageKeys.ContentTypeFor(row.Title) };

    private static DomainRuleException TooLarge(int found, DocumentArchiveOptions limits)
    {
        var megabytes = limits.MaxExportBytes / (1024 * 1024);
        var undeclaredMegabytes = DocumentArchiveOptions.UndeclaredDocumentBytes / (1024 * 1024);
        return new(ExportTooLargeCode, "ArchiveExportTooLarge", found, limits.MaxExportFiles, megabytes, undeclaredMegabytes)
        {
            Extensions = new Dictionary<string, object?>
            {
                ["fileCount"] = found,
                ["maxFiles"] = limits.MaxExportFiles,
                ["maxBytes"] = limits.MaxExportBytes,
            },
        };
    }
}
