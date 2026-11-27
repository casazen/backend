using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Extensions;

public static class DirectBookingServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenDirectBookingServices(this IServiceCollection services)
    {
        services.AddScoped<OnSiteRequestNotifier>();
        services.AddScoped<IOnSiteBookingRequestService, OnSiteBookingRequestService>();
        services.AddScoped<ICheckoutOutcomeService, CheckoutOutcomeService>();
        services.AddScoped<GuestBookingLookupService>();
        services.AddScoped<IGuestBookingLookupService>(sp => sp.GetRequiredService<GuestBookingLookupService>());
        services.AddSingleton<GuestSessionTokens>();
        services.AddScoped<IGuestSessionService, GuestSessionService>();
        services.AddOptions<GuestPhoneLookupLockoutOptions>()
            .Configure<IConfiguration>(GuestPhoneLookupLockoutOptions.Read)
            .Validate(
                options => options.IsValid(),
                $"{GuestPhoneLookupLockoutOptions.MaxFailuresKey} and {GuestPhoneLookupLockoutOptions.WindowMinutesKey} must be at least 1.")
            .ValidateOnStart();
        services.AddSingleton<GuestPhoneLookupLockout>();
        services.AddScoped<IGuestAccountBookingService, GuestAccountBookingService>();
        services.AddSingleton<ReviewLinkTokens>();
        services.AddScoped<IPublicReviewService, PublicReviewService>();
        services.AddScoped<IHostReviewService, HostReviewService>();
        services.AddScoped<IAdminReviewService, AdminReviewService>();
        services.AddScoped<IGuestMessageService, GuestMessageService>();
        services.AddScoped<IHostContactService, HostContactService>();
        services.AddScoped<NotificationRouter>();
        services.AddScoped<INotificationChannel, EmailNotificationChannel>();
        services.AddScoped<INotificationChannel, DashboardNotificationChannel>();

        return services;
    }
}
