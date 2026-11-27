using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The official ISTAT comuni and the Swiss places, the resolvers of the regime of the guest registration, and the pilot comuni of the supplier
/// registration.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class ComuniServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenComuniServices(this IServiceCollection services)
    {
        // Official ISTAT comuni list (SU-04): read side, import (admin upload and the seed file of the deploy), the SEO view,
        // the supplier matching by ISTAT code and the validated pilot comuni of the supplier registration.
        services.AddScoped<IComuneDirectory, ComuneDirectory>();
        services.AddScoped<IComuneImportService, ComuneImportService>();
        services.AddScoped<ISeoComuneCatalog, SeoComuneCatalog>();
        services.AddScoped<ISupplierComuneMatcher, SupplierComuneMatcher>();
        services.AddScoped<ISupplierPilotComuni, SupplierPilotComuni>();
        services.AddScoped<IPropertyComuneResolver, PropertyComuneResolver>();

        // The official Swiss localities and municipalities of swisstopo (E29/F01/US01/T01, NF-124): the search of the locality picker and the
        // lookup of a municipality (SwissPlacesController); the seed is loaded at startup by SwissPlacesSeedHostedService.
        services.AddScoped<ISwissPlacesDirectory, SwissPlacesDirectory>();
        // E29/F01/US01/T02: the one resolver of the place of a Swiss property (stored municipality, else derived from postal code and city), read by
        // the Ticino notification, the OTR report, the year-end count of Ticino, the policy of the identity documents and the property record.
        services.AddScoped<IPropertySwissPlaceResolver, PropertySwissPlaceResolver>();
        // E29/F02/US01/T01 (NF-125, D3): the one resolver of the regime of the registration of the guests of a property (Italy, one of the 26 cantons, or the
        // unknown Swiss place) and the writer of the host's setting; the endpoints are behind Features:GuestRegistrationRegimes, the readiness of a Swiss
        // property (the municipality, the structure number of Ticino) is not.
        services.AddScoped<IGuestRegistrationRegimeResolver, GuestRegistrationRegimeResolver>();
        services.AddScoped<IGuestNotificationOverrideService, GuestNotificationOverrideService>();

        // Pilot comuni of supplier self-serve registration (SU-01): no default, validated at startup.
        services.AddOptions<SupplierRegistrationOptions>()
            .BindConfiguration(SupplierRegistrationOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupplierRegistrationOptions>, SupplierRegistrationOptionsValidator>();

        return services;
    }
}
