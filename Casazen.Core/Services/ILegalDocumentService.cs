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
    /// falling back to Italian; null while the product owner has not provided it (the clients show "in preparation").
    /// </summary>
    LegalDocumentText? GetText(LegalDocumentKind kind, string? language);

    /// <summary>The subprocessors the running configuration actually uses (GDPR art. 28).</summary>
    SubprocessorsDocument GetSubprocessors();
}
