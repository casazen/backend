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
}
