using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The services of a property: OTA integrations, documents, photos, site profile, floor plan, points of interest, rooms, stay discounts, listing
/// text, APE, WhatsApp and the authorization of the host resources.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class PropertyServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenPropertyServices(this IServiceCollection services)
    {
        services.AddScoped<IOtaIntegrationService, OtaIntegrationService>();
        services.AddScoped<IPropertyDocumentService, PropertyDocumentService>();
        services.AddScoped<IPropertyPhotoService, PropertyPhotoService>();
        // DB-11a: the optional facts and texts of the property page of the booking site (api/properties/{id}/site-profile).
        services.AddScoped<IPropertySiteProfileService, PropertySiteProfileService>();
        services.AddScoped<IPropertyPhotoAltService, PropertyPhotoAltService>();
        services.AddScoped<IPropertyFloorPlanService, PropertyFloorPlanService>();
        services.AddScoped<IPropertyPoiService, PropertyPoiService>();
        // NF-34, NF-35: the rooms of a unit with their beds and its length-of-stay discounts (api/properties/{id}/rooms and /stay-discounts);
        // the booking service reads the tiers to price a stay. Property services, not Octorate ones.
        services.AddScoped<IPropertyRoomService, PropertyRoomService>();
        services.AddScoped<IStayDiscountService, StayDiscountService>();
        // «Suggerisci un testo» of the wizard: the AI writes the listing from the photos, the comune and the data (api/properties/{id}/listing-text).
        services.AddScoped<IPropertyListingTextService, PropertyListingTextService>();
        services.AddScoped<IApeDocumentInspector, ApeDocumentInspector>();
        services.AddScoped<IApeComplianceService, ApeComplianceService>();
        // The code of an APE the file does not show legibly, from the public CENED register of Lombardy (open data, no key).
        services.AddOptions<Casazen.Core.Options.ApeRegistryOptions>()
            .BindConfiguration(Casazen.Core.Options.ApeRegistryOptions.SectionName);
        services.AddHttpClient<IApeRegistryLookup, Casazen.Infrastructure.External.CenedApeRegistry>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CasaZen-ape-register/1.0");
        });
        services.AddOptions<WhatsAppOptions>().BindConfiguration(WhatsAppOptions.SectionName);
        services.AddHttpClient<IWhatsAppSender, WhatsAppCloudSender>(client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddScoped<IPropertyAuthorizationService, PropertyAuthorizationService>();
        services.AddScoped<IHostResourceLookup, HostResourceLookup>();
        services.AddScoped<IAdminAccessAuditService, AdminAccessAuditService>();
        services.AddScoped<IConcordatoFacileAdminService, ConcordatoFacileAdminService>();

        return services;
    }
}
