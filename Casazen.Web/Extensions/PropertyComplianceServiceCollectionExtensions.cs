using Casazen.Core.Documents;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

public static class PropertyComplianceServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenPropertyComplianceServices(this IServiceCollection services)
    {
        services.AddScoped<IPropertySafetyChecklistService, PropertySafetyChecklistService>();
        services.AddScoped<IPropertyComplianceStatusService, PropertyComplianceStatusService>();
        services.AddScoped<IPropertyOverviewService, PropertyOverviewService>();
        services.AddSingleton<IDemoEnvironmentGate, Casazen.Web.Demo.HostDemoEnvironmentGate>();
        services.AddScoped<IDemoEnvironmentService, DemoEnvironmentService>();
        services.AddScoped<ICanoneConcordatoEligibilityService, CanoneConcordatoEligibilityService>();
        services.AddScoped<IAttestationGuidanceService, AttestationGuidanceService>();
        services.AddSingleton<IPdfDocumentRenderer, MigraDocPdfDocumentRenderer>();
        services.AddScoped<IComuneImuNotificationService, ComuneImuNotificationService>();
        services.AddScoped<IRegulatoryReferenceDataAdminService, RegulatoryReferenceDataAdminService>();
        services.AddScoped<ILeaseRegistrationAuthorizationRepository, LeaseRegistrationAuthorizationRepository>();

        return services;
    }
}
