using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The host dashboard and its reports (KPIs, collections, channel costs, the day, the calendar), the lists and the history of a booking, the payment
/// list, the OTA manager and the payment service.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class HostDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenHostDashboardServices(this IServiceCollection services)
    {
        // Host dashboard KPIs per period and iCal feeds widget (PC-16).
        services.AddScoped<IHostDashboardService, HostDashboardService>();
        // The money reports of the host (NF-97): what came in by date, method and channel, the balances still to collect and the credits (E07/F03/US02/T01).
        services.AddScoped<IHostCollectionsReportService, HostCollectionsReportService>();
        // What each channel costs (NF-98, E07/F03/US02/T02): gross, commission and net, and how each channel settles its commission from the cached portal settings
        // (CasaZen's own database: nothing is asked of Octorate).
        services.AddScoped<IChannelCommissionSettingsProvider, Casazen.Infrastructure.Octorate.ChannelCommissionSettingsProvider>();
        services.AddScoped<IChannelCostsReportService, ChannelCostsReportService>();
        // The host's day in one read: stays of the day, requests, things to do (SR-03). What the cockpit items lack is a service apart.
        services.AddScoped<IHostTodayService, HostTodayService>();
        services.AddScoped<IComplianceMissingService, ComplianceMissingService>();
        // The calendar of all the host's properties in one read, and the nights taken twice the Home puts among the things to do (SR-02a).
        services.AddScoped<IHostCalendarService, HostCalendarService>();
        // Lists that hold up with many rows: bookings by days, status and text, a page at a time; payments by booking and period (SR-03).
        services.AddScoped<IBookingSearchService, BookingSearchService>();
        // The history of one booking, derived from its timestamps and the rows tied to it (SR-05a, SR-D17).
        services.AddScoped<IBookingTimelineService, BookingTimelineService>();
        services.AddScoped<IPaymentListService, PaymentListService>();
        services.AddScoped<IOtaManager, OtaManager>();
        services.AddScoped<IPaymentService, PaymentService>();

        return services;
    }
}
