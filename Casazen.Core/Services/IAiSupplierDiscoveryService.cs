namespace Casazen.Core.Services;

public interface IAiSupplierDiscoveryService
{
    Task<IReadOnlyList<ExternalSupplierSuggestion>> SearchNearbyAsync(
        string city,
        string category,
        CancellationToken cancellationToken = default);
}
