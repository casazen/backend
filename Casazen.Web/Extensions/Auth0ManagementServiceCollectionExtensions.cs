using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casazen.Web.Extensions;

public static class Auth0ManagementServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Auth0 Management API integration: a singleton client-credentials token provider
    /// (token cached until expiry − 60 s) and the role-sync service. Configuration and Railway
    /// variables are documented in <c>docs/runbooks/auth0.md</c>.
    /// </summary>
    public static IServiceCollection AddCasazenAuth0Management(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(Auth0ManagementTokenProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<IAuth0ManagementTokenProvider, Auth0ManagementTokenProvider>();
        services.AddScoped<IAuth0ManagementService, Auth0ManagementService>();
        return services;
    }
}
