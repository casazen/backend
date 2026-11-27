using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Extensions;

public static class BookingLifecycleServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenBookingLifecycleServices(this IServiceCollection services)
    {
        services.AddScoped<IManualPaymentService, ManualPaymentService>();
        services.AddScoped<PaymentRefundService>();
        services.AddScoped<IPaymentRefundService>(sp => sp.GetRequiredService<PaymentRefundService>());
        services.AddScoped<IBookingCancellationService, BookingCancellationService>();
        services.AddScoped<IGuestBookingCancellationService, GuestBookingCancellationService>();
        services.AddScoped<IHostBookingService, HostBookingService>();
        services.AddScoped<IOtaStayService, OtaStayService>();
        services.AddScoped<IHostAvailabilityService, HostAvailabilityService>();
        services.AddScoped<IManualBookingService, ManualBookingService>();
        services.AddScoped<ICalendarBlockService, CalendarBlockService>();
        services.AddScoped<IPropertyModeService, PropertyModeService>();
        services.AddScoped<ISearchAccessResolver, SearchAccessResolver>();
        services.AddScoped<Casazen.Core.Search.IGlobalSearchService, Casazen.Infrastructure.Search.GlobalSearchService>();
        services.AddScoped<IStayLifecycleService, StayLifecycleService>();
        services.AddScoped<IPaymentRefundRetryScheduler, PaymentRefundRetryScheduler>();
        services.AddScoped<PaymentRefundSubmitJob>();
        services.AddScoped<CheckoutPaymentSettlementService>();
        services.AddScoped<BookingNotifier>();
        services.AddScoped<DeferredChargeService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IStayAlertService, StayAlertService>();

        return services;
    }
}
