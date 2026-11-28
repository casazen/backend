using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.CfSimulator;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The ConcordatoFacile one-time payment, access, gift, link, privacy and status services, the completion of the lease and the simulator of the test
/// environment.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class ConcordatoFacileServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenConcordatoFacileServices(this IServiceCollection services)
    {
        services.AddOptions<Casazen.Core.Options.ConcordatoFacileOptions>()
            .BindConfiguration(Casazen.Core.Options.ConcordatoFacileOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ConcordatoFacileOptions>, Casazen.Core.Options.ConcordatoFacileOptionsValidator>();
        services.AddScoped<IConcordatoFacileCheckoutService, ConcordatoFacileCheckoutService>();
        services.AddScoped<IConcordatoFacileAccessService, ConcordatoFacileAccessService>();
        services.AddScoped<IConcordatoFacileGiftService, ConcordatoFacileGiftService>();
        services.AddScoped<IConcordatoFacileLinkService, ConcordatoFacileLinkService>();
        services.AddScoped<IConcordatoFacilePrivacyService, ConcordatoFacilePrivacyService>();
        services.AddScoped<IConcordatoFacileStatusCacheService, ConcordatoFacileStatusCacheService>();
        services.AddSingleton<ILeaseDocumentReadQueue, HangfireLeaseDocumentReadQueue>();
        services.AddScoped<IConcordatoFacileCompletionService, ConcordatoFacileCompletionService>();
        // The simulated module and status API of the test environment (CF-SIM). The status
        // cache reads the practices from IConcordatoFacileApiClient: the HTTP client of the real status API, except with
        // ConcordatoFacile:FormMode=Simulated, which the startup refuses in Production. Chosen when the client is asked for, not when the
        // application is built, like every other read of these options.
        services.AddScoped<ICfSimulatorService, CfSimulatorService>();
        services.AddScoped<SimulatedConcordatoFacileApiClient>();
        services.AddScoped<IConcordatoFacileApiClient>(provider =>
            provider.GetRequiredService<IOptions<ConcordatoFacileOptions>>().Value.FormMode == ConcordatoFacileFormMode.Simulated
                ? provider.GetRequiredService<SimulatedConcordatoFacileApiClient>()
                : provider.GetRequiredService<ConcordatoFacileApiClient>());

        return services;
    }
}
