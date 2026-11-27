using Casazen.Core.Documents;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The compliance of a property: the safety checklist, the activation status, the overview, the demo environment, the canone concordato eligibility,
/// the PDF renderer and the regulatory reference data.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class PropertyComplianceServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenPropertyComplianceServices(this IServiceCollection services)
    {
        // D.L. 145/2023 safety checklist of a short-stay property (CO-07).
        services.AddScoped<IPropertySafetyChecklistService, PropertySafetyChecklistService>();
        // Single evaluation of the activation blockers, suspension and nightly check of the published properties (CO-06).
        services.AddScoped<IPropertyComplianceStatusService, PropertyComplianceStatusService>();
        // What the host's list shows of each property besides its record: one state, what is missing, the nights (SR-07a).
        services.AddScoped<IPropertyOverviewService, PropertyOverviewService>();
        services.AddSingleton<IDemoEnvironmentGate, Casazen.Web.Demo.HostDemoEnvironmentGate>();
        services.AddScoped<IDemoEnvironmentService, DemoEnvironmentService>();
        services.AddScoped<ICanoneConcordatoEligibilityService, CanoneConcordatoEligibilityService>();
        services.AddScoped<IAttestationGuidanceService, AttestationGuidanceService>();
        // Single PDF renderer (LT-09, A7-14): A4, wrapping, pagination, embedded Unicode fonts. Stateless.
        services.AddSingleton<IPdfDocumentRenderer, MigraDocPdfDocumentRenderer>();
        services.AddScoped<IComuneImuNotificationService, ComuneImuNotificationService>();
        services.AddScoped<IRegulatoryReferenceDataAdminService, RegulatoryReferenceDataAdminService>();
        services.AddScoped<ILeaseRegistrationAuthorizationRepository, LeaseRegistrationAuthorizationRepository>();

        return services;
    }
}
