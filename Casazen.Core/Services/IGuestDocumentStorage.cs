using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public interface IGuestDocumentStorage
{
    Task<string> UploadDocumentAsync(IFormFile file, Guid orgId, Guid guestId);
    bool ValidateDocument(IFormFile file);
}
