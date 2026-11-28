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

    LegalDocumentPublication GetPublication(LegalDocumentKind kind);

    SubprocessorsDocument GetSubprocessors();
}
