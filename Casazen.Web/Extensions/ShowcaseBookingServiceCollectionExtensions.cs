using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The booking from the public showcase of a supplier: the hold, the e-mail check, the request on measure, the customer area, the upkeep jobs and the
/// reminders.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
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
        // The request on measure from the same showcase (SP-10b): no slot, the words of the customer, a thread with the supplier.
        services.AddScoped<IShowcaseInquiryService, ShowcaseInquiryService>();
        // The customer's own area of a booking (SP-11): find it with the code and the e-mail, cancel it, move it, answer a proposed time.
        services.AddScoped<IShowcaseBookingManager, ShowcaseBookingManager>();
        // The upkeep (expiry of holds and of unanswered showcase requests), the reminders of the day before and the retention of the
        // customers' data: their recurring jobs are registered whatever the flags say.
        services.AddScoped<IServiceRequestExpiryService, ServiceRequestExpiryService>();
        // The second pass of the same job (SP-10c): the supplier of a request on measure nobody answered is reminded at half of its time and ten
        // minutes before the deadline, each once.
        services.AddScoped<IShowcaseReplyReminderService, ShowcaseReplyReminderService>();
        // The other suppliers offered to the customer of a request nobody answered (SP-10c): at most three, over the one ranking of the platform.
        services.AddScoped<IShowcaseAlternativesService, ShowcaseAlternativesService>();
        // The reminder of the day before to the supplier (SP-16d) is the second pass of the same job: the reminders service takes it in.
        services.AddScoped<SupplierDayBeforeReminders>();
        services.AddScoped<IServiceRequestReminderService, ServiceRequestReminderService>();
        services.AddScoped<IServiceCustomerPrivacyService, ServiceCustomerPrivacyService>();

        return services;
    }
}
