using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the marketing module of epic E07 (CRM, consents, suppression, campaigns) is registered. Every task of the epic adds
/// its registrations here, none in <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once. Nothing here is conditional: what
/// is not ready yet sits behind <c>Features:CrmMarketing</c> where it is used (the controllers), not by being left out of DI, so the
/// services the always-on paths need (the keyed index, the suppression list) are there whatever the flag says.
/// </summary>
public static class CrmServiceCollectionExtensions
{
    public static IServiceCollection AddCrmMarketing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The Crm options, validated at startup (E07/F06/US01/T01): the key of the index, the relay domains of the OTAs, the limits of the
        // double opt-in. With the flag on, outside Development and Testing, the key is required or the application does not start.
        services.AddOptions<CrmOptions>()
            .BindConfiguration(CrmOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CrmOptions>, CrmOptionsValidator>();

        // The keyed index of the contacts and of the suppression list: HMAC-SHA256 keyed by Crm:ContactIndexKey.
        services.AddSingleton<ICrmContactIndex, CrmContactIndex>();

        return services;
    }
}
