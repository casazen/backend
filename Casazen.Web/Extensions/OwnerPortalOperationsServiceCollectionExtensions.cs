using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the owner portal operations of feature E07/F07 are registered (NF-111): the resolution of what a caller of the portal reaches, the
/// owner calendar and stays, the shared documents, the delegated contacts, the inbox and their background work. Every backend task of the feature registers what it adds here, none in
/// <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once. Nothing here is conditional: the endpoints answer 404 and the job does nothing while
/// <c>Features:AgencyTier</c> is off.
/// </summary>
public static class OwnerPortalOperationsServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOwnerPortalOperations(this IServiceCollection services)
    {
        // What the caller of the owner portal reaches, resolved again at every request (E07/F07/US01).
        services.AddScoped<IOwnerPortalAccessResolver, OwnerPortalAccessResolver>();

        // The owner calendar, the governed owner stays and their background work (E07/F07/US01/T01).
        services.AddScoped<IOwnerStayService, OwnerStayService>();
        services.AddScoped<OwnerPortalOperationsJob>();

        // The shared documents with the scan of their files, the delegated contacts and the inbox (E07/F07/US01/T02).
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
