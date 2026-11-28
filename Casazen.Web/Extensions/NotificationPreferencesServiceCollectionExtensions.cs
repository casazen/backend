using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
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

        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();

        services.AddScoped<HostMessageAlertDelivery>();
        services.AddScoped<HostMessageAlertDueJob>();
        services.AddScoped<IEventHandler<DomainEvent>, MessageReceivedNotifier>();

        return services;
    }
}
