using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the Octorate connector of epic E02 is registered (ADR-023): its services, HTTP clients, handlers and jobs.
/// Every Octorate task adds its registrations here, none in <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once.
/// Nothing here is conditional: what is not ready yet sits behind its feature flag where it is used, not by being left out of DI.
/// </summary>
public static class OctorateServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOctorate(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // OAuth tokens of the connections (E02/F01/US01/T01): the refresh call has an HttpClient of its own (30 s, https, User-Agent).
        services.AddHttpClient(OctorateTokenService.HttpClientName, OctorateHttp.Configure);
        services.AddScoped<IOctorateTokenService, OctorateTokenService>();

        // The API client (E02/F01/US01/T02), always registered: the org of every call comes from the scope of the DI scope, the
        // rate-limit buckets are the application's (one per org).
        services.AddHttpClient<IOctorateApiClient, OctorateApiClient>(OctorateHttp.Configure);
        services.AddScoped<IOctorateOrgScope, OctorateOrgScope>();
        services.AddSingleton<OctorateRateLimiter>();

        // The webhook (E02/F01/US01/T03): the secret of the address, compared in constant time; the controller needs no registration.
        services.AddSingleton<OctorateWebhookVerifier>();

        return services;
    }
}
