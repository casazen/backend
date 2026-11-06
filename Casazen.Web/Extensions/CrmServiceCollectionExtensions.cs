using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
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

        // E07/F01/US02 (NF-88, NF-89, NF-20): the matching of a guest to a CRM profile, finally registered (the profiles were never created while it
        // was not); the one writer of the contacts, with the rules of who may be written to (CrmContactRules); and the sync that keeps both in
        // step with the bookings of every source. The sync does nothing unless Features:CrmMarketing is on and the key of the index is set
        // (ICrmProfileSyncService.IsAvailable, CrmProfileSyncGate): it copies contact data into tables the erasure does not reach yet.
        services.AddScoped<IGuestMatchingService, GuestMatchingService>();
        services.AddScoped<ICrmContactService, CrmContactService>();
        services.AddScoped<ICrmProfileSyncService, CrmProfileSyncService>();

        // The sync on the event bus (a handler of every event, it filters on the booking ones): it only shortens the delay, and a failure never
        // fails the event. The recurring job crm-profile-sync (RecurringJobsRegistration) guarantees the result and backfills the guests with no
        // profile.
        services.AddScoped<IEventHandler<DomainEvent>, CrmBookingEventHandler>();
        services.AddScoped<CrmProfileSyncJob>();

        // E07/F01/US03 (NF-88): the profile API of the host screens. The read model decides the scope of the CRM (the profiles a caller reaches), the
        // list, the detail and the tags; the controller behind Features:CrmMarketing is a thin layer over it.
        services.AddScoped<ICrmProfileReadService, CrmProfileReadService>();

        return services;
    }
}
