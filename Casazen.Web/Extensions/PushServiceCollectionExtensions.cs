using Casazen.Core.Services;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;

namespace Casazen.Web.Extensions;

public static class PushServiceCollectionExtensions
{
    /// <summary>
    /// Push notifications to the app (MO-04): the Hangfire queue used by the services, the delivery job (batches of at
    /// most 100 messages), the receipts service of the recurring <c>push-receipts</c> job and the Expo HTTP client with the
    /// optional access token <c>Expo__AccessToken</c> (runbook <c>docs/runbooks/mobile-release.md</c> § 9.7).
    /// </summary>
    public static IServiceCollection AddCasazenPush(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ExpoPushOptions>().Bind(configuration.GetSection(ExpoPushOptions.SectionName));
        services.AddHttpClient<IExpoPushClient, ExpoPushClient>(client =>
        {
            client.BaseAddress = ExpoPushOptions.ApiBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // The services ask for IPushNotificationService and get the queue wrapped by the decorator that gives every push an in-app
        // notification (UI-12a); with Features:InAppNotifications off the decorator only forwards.
        services.AddScoped<HangfirePushQueue>();
        services.AddScoped<IPushNotificationService>(provider =>
            ActivatorUtilities.CreateInstance<InAppNotificationPushDecorator>(provider, provider.GetRequiredService<HangfirePushQueue>()));
        services.AddScoped<PushDeliveryJob>();
        services.AddScoped<PushReceiptService>();
        services.AddScoped<PushReceiptsJob>();

        // The bell of the shell (UI-12a): the job queued next to every push, the service behind api/me/notifications and the
        // daily retention job (runbook docs/runbooks/in-app-notifications.md).
        services.AddScoped<InAppNotificationJob>();
        services.AddScoped<IInAppNotificationService, InAppNotificationService>();
        services.AddScoped<InAppNotificationRetentionJob>();
        return services;
    }
}
