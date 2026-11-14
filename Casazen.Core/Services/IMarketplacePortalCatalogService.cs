namespace Casazen.Core.Services;

/// <summary>
/// The cache of Octorate's portal catalog (E09/F01/US01/T02, NF-54, AC-NF54-4): the marketplace reads the portals from CasaZen's own
/// database and never calls Octorate while a page loads; this refreshes that database once a day.
/// </summary>
public interface IMarketplacePortalCatalogService
{
    /// <summary>
    /// Reads the catalog from Octorate (<see cref="IOctorateApiClient.GetPortalsAsync"/>) and brings the cache in line with it, in one
    /// transaction: a portal is created or updated by its code, one with no known category is skipped (only how many is logged, never
    /// their names), and a portal the answer no longer lists is kept with <c>IsListed = false</c>. Idempotent. An error of Octorate, or an
    /// answer with no portal of a known category, leaves the cache exactly as it was: a stale cache beats an empty one. The error of
    /// Octorate is thrown as its typed error, for the job to retry.
    /// </summary>
    Task<MarketplacePortalRefreshResult> RefreshAsync(CancellationToken ct);

    /// <summary>
    /// Whether the cache holds at least one portal the marketplace lists (listed and enabled: the rule the catalog reads). <c>false</c> is a cache that
    /// no refresh ever filled, or that Octorate emptied: the portal list of the marketplace and of the wizard «Collega i tuoi canali» is empty
    /// until a refresh runs. Reads CasaZen's own database only.
    /// </summary>
    Task<bool> HasListedPortalsAsync(CancellationToken ct);
}

/// <summary>What a refresh of the portal cache did.</summary>
/// <param name="Applied"><c>false</c> when the answer held no portal of a known category and the cache was left as it was.</param>
/// <param name="Received">The portals Octorate returned.</param>
/// <param name="Stored">The portals of a known category now in the cache as listed (created or updated).</param>
/// <param name="Created">Of those, the ones that were not in the cache before.</param>
/// <param name="Hidden">The portals that were listed and are not any more.</param>
/// <param name="WithoutCategory">The portals skipped because they have no known category.</param>
public sealed record MarketplacePortalRefreshResult(bool Applied, int Received, int Stored, int Created, int Hidden, int WithoutCategory);
