using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ICal;

namespace Casazen.Web.Extensions;

/// <summary>
/// The internal activities, the import review, the activation of a property, the iCal import, export and sync of the calendars and the checkout hold
/// services.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class CalendarSyncServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenCalendarSyncServices(this IServiceCollection services)
    {
        // Internal activities performed by staff on properties (NF-22, agency tier).
        services.AddScoped<IInternalActivityService, InternalActivityService>();
        services.AddScoped<IPropertyImportReviewService, PropertyImportReviewService>();
        services.AddScoped<PropertyActivationServices>();
        services.AddScoped<PropertyActivationAccounts>();
        services.AddScoped<CalendarSyncService>();
        // The supplier's answers about the "Available" events of its calendar (they occupy until answered).
        services.AddScoped<SupplierFreeEventService>();
        // iCal import (PC-10): recurrence window, optional section ICalImport.
        services.AddOptions<ICalImportOptions>().BindConfiguration(ICalImportOptions.SectionName);
        services.AddScoped<ICalImportService>();
        services.AddScoped<ICalExportService>();
        services.AddScoped<PropertyICalSyncService>();
        services.AddScoped<ICheckoutHoldExpiryService, CheckoutHoldExpiryService>();
        services.AddScoped<ICheckoutPaymentReconciliationService, CheckoutPaymentReconciliationService>();

        return services;
    }
}
