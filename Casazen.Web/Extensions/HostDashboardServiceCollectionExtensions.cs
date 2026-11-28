using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

public static class HostDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenHostDashboardServices(this IServiceCollection services)
    {
        services.AddScoped<IHostDashboardService, HostDashboardService>();
        services.AddScoped<IHostCollectionsReportService, HostCollectionsReportService>();
        services.AddScoped<IChannelCommissionSettingsProvider, Casazen.Infrastructure.Octorate.ChannelCommissionSettingsProvider>();
        services.AddScoped<IChannelCostsReportService, ChannelCostsReportService>();
        services.AddScoped<IHostTodayService, HostTodayService>();
        services.AddScoped<IComplianceMissingService, ComplianceMissingService>();
        services.AddScoped<IHostCalendarService, HostCalendarService>();
        services.AddScoped<IBookingSearchService, BookingSearchService>();
        services.AddScoped<IBookingTimelineService, BookingTimelineService>();
        services.AddScoped<IPaymentListService, PaymentListService>();
        services.AddScoped<IOtaManager, OtaManager>();
        services.AddScoped<IPaymentService, PaymentService>();

        return services;
    }
}
