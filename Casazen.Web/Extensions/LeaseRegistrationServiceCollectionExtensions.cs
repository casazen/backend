using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The registration of a lease and the fiscal side of the long-term area: the cedolare advisory, the RLI export and registration, the Questura
/// communication, the Openapi client, the fiscal regime service, the tourist tax summary and the document archive.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class LeaseRegistrationServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenLeaseRegistrationServices(this IServiceCollection services)
    {
        services.AddOptions<Casazen.Core.Options.CedolareAdvisoryOptions>()
            .BindConfiguration(Casazen.Core.Options.CedolareAdvisoryOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.CedolareAdvisoryOptions>, Casazen.Core.Options.CedolareAdvisoryOptionsValidator>();
        services.AddScoped<ICedolareAdvisoryService, CedolareAdvisoryService>();
        services.AddScoped<IRliExportService, RliExportService>();
        services.AddScoped<IRliChecklistService, RliChecklistService>();
        // Questura communication for an extra-EU tenant (LT-07): declared by the landlord, never inferred from a reminder.
        services.AddScoped<IQuesturaCommunicationService, QuesturaCommunicationService>();
        services.AddOptions<OpenapiOptions>()
            .BindConfiguration(OpenapiOptions.SectionName);
        services.AddHttpClient<IOpenapiDocuEngineClient, OpenapiDocuEngineClient>((sp, client) =>
        {
            var configured = sp.GetRequiredService<IOptions<OpenapiOptions>>().Value.BaseUrl;
            if (Uri.TryCreate(configured, UriKind.Absolute, out var baseUri))
                client.BaseAddress = baseUri;
        });
        services.AddScoped<IOpenapiRegistrationPreparation, OpenapiRegistrationPreparation>();
        services.AddScoped<IOpenapiRegistrationPaymentGate, OpenapiRegistrationPaymentGate>();
        services.AddScoped<IOpenapiRegistrationCheckoutService, OpenapiRegistrationCheckoutService>();
        services.AddScoped<IRliRegistrationService, RliRegistrationService>();
        services.AddSingleton<UnconfiguredLeaseRegistrationProvider>();
        services.AddScoped<ILeaseRegistrationProvider>(sp => new OpenapiAwareLeaseRegistrationProvider(
            sp.GetRequiredService<UnconfiguredLeaseRegistrationProvider>(),
            sp.GetRequiredService<IOpenapiDocuEngineClient>()));
        services.AddScoped<IFiscalRegimeService, FiscalService>();
        services.AddScoped<IFiscalReportingService>(sp => (FiscalService)sp.GetRequiredService<IFiscalRegimeService>());
        services.AddScoped<ITouristTaxSummaryService, TouristTaxSummaryService>();
        services.AddDocumentArchive();

        return services;
    }
}
