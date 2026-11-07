using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The document archive (NF-99, REP-04, E07/F03/US03): every document the caller may read in one place, as a read model over the tables of the
/// sources (<see cref="IDocumentArchiveSource"/>): one SQL statement for the list whatever the number of sources, filtered and paged in the
/// database, no file copied and no second index kept. The service never checks roles: the web layer decides which types the caller may read
/// (<see cref="DocumentArchiveAccess"/>) and which properties it reaches (<see cref="HostScope"/>), and every method here applies both, so the
/// list, the count, the download and the export show exactly the same documents to the same caller.
/// </summary>
public interface IDocumentArchiveService
{
    /// <summary>
    /// A page of the documents the caller may read, newest day first and, for the same day, by id: the sources of the allowed types (and of the
    /// types asked for) concatenated, filtered by days, property, booking and search, counted and paged in SQL.
    /// </summary>
    Task<DocumentArchivePage> ListAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many documents each type the caller may read has, for the filters of <paramref name="query"/> except its type (so the chips of the
    /// types show what choosing one would give). Every type the caller may read is there, with zero when it has no document.
    /// </summary>
    Task<IReadOnlyList<ArchiveTypeCount>> CountByTypeAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The file of one document. 403 <c>archive_type_forbidden</c> for a type the caller may not read; 404 <c>archive_document_not_found</c>
    /// for a row that is missing, of another org or outside the scope (the same row the list would show or not show); 404
    /// <c>archive_document_has_no_file</c> for a row with no file (or whose file is gone). The caller disposes the stream.
    /// </summary>
    Task<ArchiveFile> OpenAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        ArchiveDocumentType type,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a ZIP of the documents <paramref name="query"/> selects (the page of the query is ignored) to <paramref name="destination"/>,
    /// straight from the storage, never the whole archive in memory (the destination needs no seek and no synchronous write: the response of
    /// the API has neither): a folder for each type and <c>indice.csv</c> at the root. Beyond the caps
    /// of <c>DocumentArchive:MaxExportFiles</c> and <c>DocumentArchive:MaxExportBytes</c> it throws 422 <c>archive_export_too_large</c> with
    /// the number of documents found, before a byte is written.
    /// </summary>
    Task<DocumentArchiveExportResult> ExportAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery query,
        Stream destination,
        CancellationToken cancellationToken = default);
}
