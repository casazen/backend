using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F03 (NF-65): <c>AddNotificationPreferences</c> registers exactly the decision point, the delivery of the message alerts, the job of what the quiet
/// hours hold back and the handler of the messages of the channels; every line of this list is a change someone makes on purpose. The registrations resolve
/// together with <c>GuestMessageService</c>, which depends on the delivery.
/// </summary>
public class NotificationPreferencesRegistrationTests
{
    [Fact]
    public void AddNotificationPreferences_RegistersTheDecisionPointTheDeliveryTheJobAndTheHandler_AllScoped()
    {
        var services = new ServiceCollection();

        services.AddNotificationPreferences();

        Assert.Equal(
            [
                (typeof(INotificationPreferenceService), typeof(NotificationPreferenceService)),
                (typeof(HostMessageAlertDelivery), typeof(HostMessageAlertDelivery)),
                (typeof(HostMessageAlertDueJob), typeof(HostMessageAlertDueJob)),
                (typeof(IEventHandler<DomainEvent>), typeof(MessageReceivedNotifier)),
            ],
            services.Select(d => (d.ServiceType, d.ImplementationType ?? d.ServiceType)).ToList());
        Assert.All(services, d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));
    }

    [Fact]
    public void TheRegistrations_ResolveWithTheirDependencies_AndGuestMessageServiceGetsTheDelivery()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(Mock.Of<IContextAuthorizationService>());
        services.AddSingleton(Mock.Of<IPushNotificationService>());
        services.AddSingleton(Mock.Of<IEmailQueue>());
        services.AddSingleton(Mock.Of<IInAppNotificationPublisher>());
        services.AddSingleton(Mock.Of<IInAppNotificationService>());
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddSingleton(new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://casazen-app.test" })));
        services.AddScoped<GuestMessageService>();
        services.AddNotificationPreferences();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        Assert.IsType<NotificationPreferenceService>(scope.ServiceProvider.GetRequiredService<INotificationPreferenceService>());
        Assert.IsType<MessageReceivedNotifier>(Assert.Single(scope.ServiceProvider.GetServices<IEventHandler<DomainEvent>>()));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<HostMessageAlertDueJob>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GuestMessageService>());
    }
}
