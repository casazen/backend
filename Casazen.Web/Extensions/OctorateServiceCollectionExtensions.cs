using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Options;

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

        // The token of the application itself (the partner login, E09/F01/US01/T02): one for the whole process, reused while valid.
        services.AddSingleton<OctoratePartnerTokenCache>();

        // The API client (E02/F01/US01/T02), always registered: the org of every call comes from the scope of the DI scope, the
        // rate-limit buckets are the application's (one per org).
        services.AddHttpClient<IOctorateApiClient, OctorateApiClient>(OctorateHttp.Configure);
        services.AddScoped<IOctorateOrgScope, OctorateOrgScope>();
        services.AddSingleton<OctorateRateLimiter>();

        // The webhook (E02/F01/US01/T03): the secret of the address, compared in constant time; the controller needs no registration.
        services.AddSingleton<OctorateWebhookVerifier>();

        // The consent and the connection (E02/F01/US02/T01): the Octorate options, validated at startup with the flag on, the
        // single-use states and the connect service. The code exchange uses the HttpClient of the token service.
        services.AddOptions<OctorateOptions>()
            .BindConfiguration(OctorateOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OctorateOptions>, OctorateOptionsValidator>();
        services.AddScoped<OctorateOAuthStateService>();
        services.AddScoped<IOctorateConnectService, OctorateConnectService>();

        // The mapping of the listings to the CasaZen properties and the import (E02/F01/US02/T03); thresholds in Octorate:Mapping.
        services.AddScoped<IChannelMappingService, ChannelMappingService>();

        return services;
    }
}
