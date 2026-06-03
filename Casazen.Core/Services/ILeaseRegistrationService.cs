using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface ILeaseRegistrationService
{
    Task<string> SubmitRegistrationAsync(LeaseContract lease);
    Task<RegistrationStatusResult> PollStatusAsync(string externalRegistrationId);
    Task<Stream> DownloadReceiptAsync(string externalRegistrationId);
}

public record RegistrationStatusResult(
    string ExternalRegistrationId,
    string Status,
    string? RegistrationCode,
    bool IsConfirmed);
