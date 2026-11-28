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
    Task<DocumentArchivePage> ListAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArchiveTypeCount>> CountByTypeAsync(
        HostScope scope,
        DocumentArchiveAccess access,
        DocumentArchiveQuery query,
        CancellationToken cancellationToken = default);

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
