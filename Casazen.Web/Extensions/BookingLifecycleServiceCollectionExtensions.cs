using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Extensions;

/// <summary>
/// What happens to a booking after it is made: payments received outside CasaZen, refunds and cancellations, edits and manual bookings, the scheduled
/// change of rental mode, the global search, the late checkout payments, the confirmation emails, the deferred charge and the stay alerts.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class BookingLifecycleServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenBookingLifecycleServices(this IServiceCollection services)
    {
        // A payment the host received outside CasaZen (cash, bank transfer): born Completed, capped to the balance, once per key (SR-06b).
        services.AddScoped<IManualPaymentService, ManualPaymentService>();
        // Refunds and cancellations on Stripe Connect (BK-02).
        services.AddScoped<PaymentRefundService>();
        services.AddScoped<IPaymentRefundService>(sp => sp.GetRequiredService<PaymentRefundService>());
        services.AddScoped<IBookingCancellationService, BookingCancellationService>();
        // Guest self-service cancellation via signed link (BK-02, BK-07, PO 2026-10-08).
        services.AddScoped<IGuestBookingCancellationService, GuestBookingCancellationService>();
        // Host changes to a booking: edit, confirm, check-out (PC-07).
        services.AddScoped<IHostBookingService, HostBookingService>();
        services.AddScoped<IOtaStayService, OtaStayService>();
        // Whether a stay of the host's property is free, and the booking the host enters by hand with only the name and the email (SR-06a).
        services.AddScoped<IHostAvailabilityService, HostAvailabilityService>();
        services.AddScoped<IManualBookingService, ManualBookingService>();
        services.AddScoped<ICalendarBlockService, CalendarBlockService>();
        // Scheduled change of rental mode of a property: preview, creation, the hourly application (PM-02).
        services.AddScoped<IPropertyModeService, PropertyModeService>();
        // Global search of the palette: the groups the caller may read (policies) and the SQL (UI-13a).
        services.AddScoped<ISearchAccessResolver, SearchAccessResolver>();
        services.AddScoped<Casazen.Core.Search.IGlobalSearchService, Casazen.Infrastructure.Search.GlobalSearchService>();
        services.AddScoped<IStayLifecycleService, StayLifecycleService>();
        services.AddScoped<IPaymentRefundRetryScheduler, PaymentRefundRetryScheduler>();
        services.AddScoped<PaymentRefundSubmitJob>();
        // Late checkout payments: confirmed again or refunded in full (BK-04).
        services.AddScoped<CheckoutPaymentSettlementService>();
        // Booking confirmation (guest + host) and cancellation emails (BK-10).
        services.AddScoped<BookingNotifier>();
        // Deferred charge of "Paga alla scadenza" bookings: job and webhooks (BK-08).
        services.AddScoped<DeferredChargeService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IStayAlertService, StayAlertService>();

        return services;
    }
}
