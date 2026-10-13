using Casazen.Core.Models;

namespace Casazen.Core.Services;

public interface ILegalDocumentService
{
    LegalDocumentMeta GetTos();
    LegalDocumentMeta GetPrivacy();
    LegalDocumentMeta GetDpa();
    LegalDocumentMeta Get(LegalDocumentKind kind);

    /// <summary>
    /// Text of the current version of <paramref name="kind"/> in <paramref name="language"/> (<c>it</c> or <c>en</c>),
    /// falling back to Italian; null while there is no text of the version or while a value it needs is not configured
    /// (fail-closed: the clients show "in preparation", never a placeholder).
    /// </summary>
    LegalDocumentText? GetText(LegalDocumentKind kind, string? language);

    /// <summary>
    /// Whether the text of the configured version of <paramref name="kind"/> is published, and if not why (missing file,
    /// missing configuration of the controller's data, malformed text). Names variables, never values: health check and
    /// startup log (D9).
    /// </summary>
    LegalDocumentPublication GetPublication(LegalDocumentKind kind);

    /// <summary>The subprocessors the running configuration actually uses (GDPR art. 28).</summary>
    SubprocessorsDocument GetSubprocessors();
}
