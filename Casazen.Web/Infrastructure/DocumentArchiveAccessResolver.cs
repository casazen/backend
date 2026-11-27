using Casazen.Core.Services;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Decides which types of the document archive the caller may read (NF-99, E07/F03/US03/T01): the web layer asks, for the context and the
/// permission of every registered source (<see cref="IDocumentArchiveSource.ContextKey"/>, <see cref="IDocumentArchiveSource.PermissionKey"/>),
/// whether the caller holds it, and the service reads only the types this says. The door of the controller lets every host reader in; each type
/// is then opened by the permission of the area its documents belong to, so the Accountant (<c>property.read</c>, <c>booking.read</c>,
/// <c>payment.read</c>, <c>lease.read</c>, no <c>guest.read</c>) reads the tax, property and lease documents and never a compliance proof, and
/// a collaborator reads only what its role reads.
/// </summary>
public static class DocumentArchiveAccessResolver
{
    /// <summary>Separates the alternative contexts of a source (<c>short-rent|long-rent</c>).</summary>
    private const char ContextSeparator = '|';

    /// <summary>
    /// The types <paramref name="userId"/> may read. A type is open when the caller holds the permission of <b>every</b> source of that type,
    /// because the service reads all the sources of an allowed type: a type shared by two sources with two permissions is never opened by one of
    /// them. A question that cannot be answered counts as «no» (<see cref="IContextAuthorizationService.HasPermissionAsync"/> fails closed), so
    /// the caller sees less, never more. A caller with no user id reads nothing.
    /// </summary>
    public static async Task<DocumentArchiveAccess> ResolveAsync(
        IEnumerable<IDocumentArchiveSource> sources,
        IContextAuthorizationService authorization,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(authorization);

        if (string.IsNullOrWhiteSpace(userId))
            return DocumentArchiveAccess.None;

        // One question for each distinct permission, whatever the number of sources that share it.
        var answers = new Dictionary<(string Context, string Permission), bool>();
        var open = new HashSet<ArchiveDocumentType>();
        var closed = new HashSet<ArchiveDocumentType>();
        foreach (var source in sources)
        {
            if (closed.Contains(source.Type))
                continue;

            if (await MayReadAsync(source, authorization, userId, answers, cancellationToken))
            {
                open.Add(source.Type);
            }
            else
            {
                open.Remove(source.Type);
                closed.Add(source.Type);
            }
        }

        return new DocumentArchiveAccess(open);
    }

    private static async Task<bool> MayReadAsync(
        IDocumentArchiveSource source,
        IContextAuthorizationService authorization,
        string userId,
        Dictionary<(string Context, string Permission), bool> answers,
        CancellationToken cancellationToken)
    {
        // Any of the contexts of the source grants it (the property core is read in either rental context).
        foreach (var context in source.ContextKey.Split(ContextSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = (context, source.PermissionKey);
            if (!answers.TryGetValue(key, out var held))
            {
                held = await authorization.HasPermissionAsync(userId, context, source.PermissionKey, cancellationToken);
                answers[key] = held;
            }

            if (held)
                return true;
        }

        return false;
    }
}
