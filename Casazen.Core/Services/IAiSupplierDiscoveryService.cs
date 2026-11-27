namespace Casazen.Core.Services;

public interface IAiSupplierDiscoveryService
{
    Task<IReadOnlyList<ExternalSupplierSuggestion>> SearchNearbyAsync(
        Guid orgId,
        string city,
        string category,
        CancellationToken cancellationToken = default);
}
