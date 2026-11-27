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
        // ConcordatoFacile one-time payment (ADR-005): Stripe Checkout with validated Price ID configuration.
        services.AddOptions<Casazen.Core.Options.ConcordatoFacileOptions>()
            .BindConfiguration(Casazen.Core.Options.ConcordatoFacileOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ConcordatoFacileOptions>, Casazen.Core.Options.ConcordatoFacileOptionsValidator>();
        services.AddScoped<IConcordatoFacileCheckoutService, ConcordatoFacileCheckoutService>();
        services.AddScoped<IConcordatoFacileAccessService, ConcordatoFacileAccessService>();
        services.AddScoped<IConcordatoFacileGiftService, ConcordatoFacileGiftService>();
        services.AddScoped<IConcordatoFacileLinkService, ConcordatoFacileLinkService>();
        // The privacy notice accepted for a lease before the guided form is opened; the signed link and the controller take it (CF-BE).
        services.AddScoped<IConcordatoFacilePrivacyService, ConcordatoFacilePrivacyService>();
        // The practices of a lease and the lookup of a practice, read from the ConcordatoFacile status API and kept in memory (ADR-008). The
        // controller of the module takes it in its constructor: without this registration every one of its actions fails (CF-FIX2).
        services.AddScoped<IConcordatoFacileStatusCacheService, ConcordatoFacileStatusCacheService>();
        // The lease of the guided service follows its practice: the documents read into the draft, the contract imported as signed once the
        // practice says it is, the registration declared from the receipt (CF-DONE; `POST …/concordatofacile/sync`).
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
