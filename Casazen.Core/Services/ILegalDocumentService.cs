using Casazen.Core.Models;

namespace Casazen.Core.Services;

public interface ILegalDocumentService
{
    LegalDocumentMeta GetTos();
    LegalDocumentMeta GetPrivacy();
    LegalDocumentMeta GetDpa();
    SubprocessorsDocument GetSubprocessors();
}
