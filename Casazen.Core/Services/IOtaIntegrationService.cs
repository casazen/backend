using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IOtaIntegrationService
{
    Task<IEnumerable<OtaIntegration>> GetPropertyIntegrationsAsync(Guid propertyId);
    Task<OtaIntegration?> GetIntegrationAsync(Guid id);
    Task<OtaIntegration> CreateIntegrationAsync(Guid propertyId, string platform, string externalPropertyId, string apiKey);
    Task UpdateIntegrationAsync(Guid id, string? externalPropertyId, string? apiKey, bool? isActive);
    Task DeleteIntegrationAsync(Guid id);
    string MaskApiKey(string apiKey);
}
