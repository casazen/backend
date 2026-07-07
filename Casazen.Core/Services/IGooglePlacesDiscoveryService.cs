namespace Casazen.Core.Services;

public interface IGooglePlacesDiscoveryService
{
    Task<IReadOnlyList<ExternalSupplierSuggestion>> SearchNearbyAsync(
        string city,
        string category,
        CancellationToken cancellationToken = default);
}
