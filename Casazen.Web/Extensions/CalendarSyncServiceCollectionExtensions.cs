using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ICal;

namespace Casazen.Web.Extensions;

public static class CalendarSyncServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenCalendarSyncServices(this IServiceCollection services)
    {
        services.AddScoped<IInternalActivityService, InternalActivityService>();
        services.AddScoped<IPropertyImportReviewService, PropertyImportReviewService>();
        services.AddScoped<PropertyActivationServices>();
        services.AddScoped<PropertyActivationAccounts>();
        services.AddScoped<CalendarSyncService>();
        services.AddScoped<SupplierFreeEventService>();
        services.AddOptions<ICalImportOptions>().BindConfiguration(ICalImportOptions.SectionName);
        services.AddScoped<ICalImportService>();
        services.AddScoped<ICalExportService>();
        services.AddScoped<PropertyICalSyncService>();
        services.AddScoped<ICheckoutHoldExpiryService, CheckoutHoldExpiryService>();
        services.AddScoped<ICheckoutPaymentReconciliationService, CheckoutPaymentReconciliationService>();

        return services;
    }
}
