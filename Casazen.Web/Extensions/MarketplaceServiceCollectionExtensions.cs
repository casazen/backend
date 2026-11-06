using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Resources;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the marketplace of epic E09 is registered (NF-54 to NF-59): its services and jobs. Every backend task of the epic
/// registers what it adds here, none in <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once. Nothing here is conditional:
/// what depends on the Octorate connector sits behind <c>Features:OctorateConnector</c> where it is used (the daily job is scheduled only
/// while the flag is on, the endpoints that need Octorate answer 404 or say so), not by being left out of the container.
/// </summary>
public static class MarketplaceServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenMarketplace(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The cache of Octorate's portal catalog and its daily refresh (E09/F01/US01/T02).
        services.AddScoped<IMarketplacePortalCatalogService, MarketplacePortalCatalogService>();
        services.AddScoped<MarketplacePortalRefreshJob>();

        // The words of the marketplace in the language the page asks for, and the catalog with the Active tab (E09/F01/US01/T03).
        services.AddSingleton<IMarketplaceTexts, ResxMarketplaceTexts>();
        services.AddScoped<IMarketplaceCatalogService, MarketplaceCatalogService>();

        // The detail card of an entry (E09/F01/US01/T04).
        services.AddScoped<IMarketplaceEntryDetailService, MarketplaceEntryDetailService>();

        return services;
    }
}
