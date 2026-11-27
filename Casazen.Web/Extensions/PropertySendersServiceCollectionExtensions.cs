using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// Who sends the police and ISTAT communications of a property: the Questura credentials, the sender of the property, the delegation, the health of
/// the connection and the ISTAT region table.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class PropertySendersServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenPropertySenderServices(this IServiceCollection services)
    {
        // Alloggiati Web credentials of a property: write-only, encrypted at rest (CO-14, A5-30).
        services.AddScoped<IQuesturaCredentialsService, QuesturaCredentialsService>();
        // Who sends the police and ISTAT communications of a property (stored, or the default of PO-02) through one write path, and the
        // delegation of the Alloggiati transmission of the org (ADR-029, AW-R02, E03/F01/US05/T02). The sender switch with its checks
        // and the endpoints sit on top of them (E03/F01/US02/T01).
        services.AddScoped<DirectPortalRoutes>();
        services.AddScoped<IDirectAlloggiatiRoute>(sp => sp.GetRequiredService<DirectPortalRoutes>());
        services.AddScoped<IRoss1000DirectRoute>(sp => sp.GetRequiredService<DirectPortalRoutes>());
        services.AddScoped<IPropertySenderStore, PropertySenderStore>();
        services.AddScoped<IAlloggiatiDelegationService, AlloggiatiDelegationService>();
        // The sender of a property for the host (E03/F01/US02/T01): its state, the switch with the confirmation and the delegation, the connection
        // test against Octorate and the declaration «Activated». The sender «CasaZen sends» is not offered in this release (Questura accounts postponed).
        services.AddScoped<IPropertySenderService, PropertySenderService>();
        // Whether the police and ISTAT send of a property is really active (E03/F02/US01/T02, NF-52): the sender view turned into a state per
        // communication (Active only on Octorate's word, Unknown when it is not readable), with a short memory for the status engine.
        services.AddScoped<IComplianceConnectionHealthService, ComplianceConnectionHealthService>();
        // The ISTAT region table and what a property gets from it (E21/F01/US01/T01, decision O6): the rows the staff keeps, and the region, deadline,
        // required guest fields, coverage and next step of a property. Reads and writes the table only; nothing is sent to anybody.
        services.AddScoped<IIstatRegionService, IstatRegionService>();

        return services;
    }
}
