namespace Casazen.Core.Services;

public interface IAiSupplierDiscoveryService
{
    /// <param name="orgId">
    /// The org that asked: its results are cached under its own key and cap (<see cref="IAiResponseCache"/>, A8-25), never
    /// served to another org.
    /// </param>
    Task<IReadOnlyList<ExternalSupplierSuggestion>> SearchNearbyAsync(
        Guid orgId,
        string city,
        string category,
        CancellationToken cancellationToken = default);
}
