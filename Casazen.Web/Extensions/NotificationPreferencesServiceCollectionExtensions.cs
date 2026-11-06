using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The notification preferences of the host users and what already uses them (epic E05, feature F03, NF-65, ADR-025): the one service that decides
/// who is told what and how, and the alerts of the new messages of the guests, OTA and direct. An extension class of its own, so this feature never
/// collides with the shared <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once. The later alerts of the epic (an approval to give, a
/// draft ready, a scheduled message that failed) add their registrations here and ask the same service.
/// </summary>
public static class NotificationPreferencesServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationPreferences(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // E05/F03/US01/T02 (NF-65): the choices of each user, the PO-10 defaults, and the audience of an event with the quiet hours and the
        // permission applied. HostNotifyingPathsArchitectureTests fails when a path that notifies a host about one of the eight events does not ask it.
        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();

        return services;
    }
}
