using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

public static class PropertyServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenPropertyServices(this IServiceCollection services)
    {
        services.AddScoped<IOtaIntegrationService, OtaIntegrationService>();
        services.AddScoped<IPropertyDocumentService, PropertyDocumentService>();
        services.AddScoped<IPropertyPhotoService, PropertyPhotoService>();
        services.AddScoped<IPropertySiteProfileService, PropertySiteProfileService>();
        services.AddScoped<IPropertyPhotoAltService, PropertyPhotoAltService>();
        services.AddScoped<IPropertyFloorPlanService, PropertyFloorPlanService>();
        services.AddScoped<IPropertyPoiService, PropertyPoiService>();
        services.AddScoped<IPropertyRoomService, PropertyRoomService>();
        services.AddScoped<IStayDiscountService, StayDiscountService>();
        services.AddScoped<IPropertyListingTextService, PropertyListingTextService>();
        services.AddScoped<IApeDocumentInspector, ApeDocumentInspector>();
        services.AddScoped<IApeComplianceService, ApeComplianceService>();
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
