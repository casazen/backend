using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

public static class ShowcaseBookingServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenShowcaseBookingServices(this IServiceCollection services)
    {
        // Booking from a supplier's public showcase (SP-10, flag SupplierShowcaseBooking): hold, e-mail check, request. The options
        // (Suppliers:Showcase, and Suppliers:CustomerIndexKey for the HMAC of the customers' e-mail) are validated at startup: with the
        // flag on, the version of the privacy notice and (outside Development and Testing) the key are required.
        services.AddOptions<Casazen.Core.Options.ShowcaseBookingOptions>()
            .BindConfiguration(Casazen.Core.Options.ShowcaseBookingOptions.SectionName)
            .ValidateOnStart();
        services.AddOptions<Casazen.Core.Options.ServiceCustomerIndexOptions>()
            .BindConfiguration(Casazen.Core.Options.ServiceCustomerIndexOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Casazen.Core.Options.ShowcaseBookingOptionsValidator>();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ShowcaseBookingOptions>>(
            provider => provider.GetRequiredService<Casazen.Core.Options.ShowcaseBookingOptionsValidator>());
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ServiceCustomerIndexOptions>>(
            provider => provider.GetRequiredService<Casazen.Core.Options.ShowcaseBookingOptionsValidator>());
        services.AddSingleton<IServiceCustomerIndex, ServiceCustomerIndex>();
        services.AddScoped<IShowcaseHoldReader, ShowcaseHoldReader>();
        services.AddScoped<IServiceCustomerReader, ServiceCustomerReader>();
        services.AddScoped<ShowcaseBookingNotifier>();
        services.AddScoped<IShowcaseBookingService, ShowcaseBookingService>();
        services.AddScoped<IShowcaseInquiryService, ShowcaseInquiryService>();
        services.AddScoped<IShowcaseBookingManager, ShowcaseBookingManager>();
        services.AddScoped<IServiceRequestExpiryService, ServiceRequestExpiryService>();
        services.AddScoped<IShowcaseReplyReminderService, ShowcaseReplyReminderService>();
        services.AddScoped<IShowcaseAlternativesService, ShowcaseAlternativesService>();
        services.AddScoped<SupplierDayBeforeReminders>();
        services.AddScoped<IServiceRequestReminderService, ServiceRequestReminderService>();
        services.AddScoped<IServiceCustomerPrivacyService, ServiceCustomerPrivacyService>();

        return services;
    }
}
