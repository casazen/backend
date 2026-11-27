using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The feature flags, the user, property and booking services, and the read side of the public booking site (availability, calendar of a property and
/// search).
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class BookingCoreServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenBookingCoreServices(this IServiceCollection services)
    {
        // Features:* flags (FD-20)
        services.AddSingleton<IFeatureFlags, ConfigurationFeatureFlags>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IBookingService, BookingService>();
        // Public availability of the booking site, same nights as the booking checks (BK-05).
        services.AddScoped<IPublicAvailabilityService, PublicAvailabilityService>();
        // The calendar of a property on the booking site: availability and price of every night, from the quote's own functions (DB-03b).
        services.AddScoped<IPublicCalendarService, PublicCalendarService>();
        // The search of a booking site (the houses for a stay, the calendar of the whole site, the places, the houses that look alike) and
        // the small cache that keeps its answers for a few seconds (DB-07a).
        services.AddSingleton<PublicReadCache>();
        services.AddScoped<IPublicSiteSearchService, PublicSiteSearchService>();

        return services;
    }
}
