using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

public static class OwnerPortalOperationsServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOwnerPortalOperations(this IServiceCollection services)
    {
        services.AddScoped<IOwnerPortalAccessResolver, OwnerPortalAccessResolver>();

        services.AddScoped<IOwnerStayService, OwnerStayService>();
        services.AddScoped<OwnerPortalOperationsJob>();

        services.AddSingleton<IOwnerFileScanner, OwnerFileContentScanner>();
        services.AddScoped<IOwnerCollaborationService, OwnerCollaborationService>();

        // The options of the owner portal, validated at startup: with Features:AgencyTier on, outside Development and Testing, the key of the keyed index of the
        // contacts is required or the application does not start. The index is HMAC-SHA256 keyed by OwnerPortal:ContactIndexKey (the address is encrypted at rest).
        services.AddOptions<OwnerPortalOptions>()
            .BindConfiguration(OwnerPortalOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OwnerPortalOptions>, OwnerPortalOptionsValidator>();
        services.AddSingleton<IOwnerContactIndex, OwnerContactIndex>();

        return services;
    }
}
