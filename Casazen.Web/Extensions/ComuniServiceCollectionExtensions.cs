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

        services.AddScoped<ISwissPlacesDirectory, SwissPlacesDirectory>();
        services.AddScoped<IPropertySwissPlaceResolver, PropertySwissPlaceResolver>();
        services.AddScoped<IGuestRegistrationRegimeResolver, GuestRegistrationRegimeResolver>();
        services.AddScoped<IGuestNotificationOverrideService, GuestNotificationOverrideService>();

        services.AddOptions<SupplierRegistrationOptions>()
            .BindConfiguration(SupplierRegistrationOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupplierRegistrationOptions>, SupplierRegistrationOptionsValidator>();

        return services;
    }
}
