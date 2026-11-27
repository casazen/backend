using Casazen.Core.Services;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.AddScoped<HangfirePushQueue>();
        services.AddScoped<IPushNotificationService>(provider =>
            ActivatorUtilities.CreateInstance<InAppNotificationPushDecorator>(provider, provider.GetRequiredService<HangfirePushQueue>()));
        services.AddScoped<PushDeliveryJob>();
        services.AddScoped<PushReceiptService>();
        services.AddScoped<PushReceiptsJob>();

        services.AddScoped<InAppNotificationJob>();
        services.AddScoped<IInAppNotificationService, InAppNotificationService>();
        services.AddScoped<InAppNotificationRetentionJob>();

        services.AddSignalR();
        services.TryAddSingleton<IInAppNotificationPublisher, NoOpInAppNotificationPublisher>();
        services.Replace(ServiceDescriptor.Singleton<IInAppNotificationPublisher, SignalRInAppNotificationPublisher>());

        services.TryAddSingleton<IPopupAlertPublisher, NoOpPopupAlertPublisher>();
        services.Replace(ServiceDescriptor.Singleton<IPopupAlertPublisher, SignalRPopupAlertPublisher>());
        return services;
    }
}
