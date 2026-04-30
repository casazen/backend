using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public class OtaIntegrationService(IOtaIntegrationRepository repository) : IOtaIntegrationService
{
    public async Task<IEnumerable<OtaIntegration>> GetPropertyIntegrationsAsync(Guid propertyId)
    {
        return await repository.GetByPropertyIdAsync(propertyId);
    }

    public async Task<OtaIntegration?> GetIntegrationAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<OtaIntegration> CreateIntegrationAsync(Guid propertyId, string platform, string externalPropertyId, string apiKey)
    {
        var integration = new OtaIntegration
        {
            PropertyId = propertyId,
            Platform = platform,
            ExternalPropertyId = externalPropertyId,
            ApiKey = apiKey,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        return await repository.CreateAsync(integration);
    }

    public async Task UpdateIntegrationAsync(Guid id, string? externalPropertyId, string? apiKey, bool? isActive)
    {
        var integration = await repository.GetByIdAsync(id);
        if (integration == null)
            throw new KeyNotFoundException($"OTA integration {id} not found");

        if (externalPropertyId != null)
            integration.ExternalPropertyId = externalPropertyId;

        if (apiKey != null)
            integration.ApiKey = apiKey;

        if (isActive.HasValue)
            integration.IsActive = isActive.Value;

        await repository.UpdateAsync(integration);
    }

    public async Task DeleteIntegrationAsync(Guid id)
    {
        await repository.DeleteAsync(id);
    }

    public string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length <= 8)
            return "****";

        return $"{apiKey[..4]}****{apiKey[^4..]}";
    }
}
