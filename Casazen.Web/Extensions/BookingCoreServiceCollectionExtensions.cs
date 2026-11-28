using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

public static class BookingCoreServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenBookingCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IFeatureFlags, ConfigurationFeatureFlags>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IPublicAvailabilityService, PublicAvailabilityService>();
        services.AddScoped<IPublicCalendarService, PublicCalendarService>();
        services.AddSingleton<PublicReadCache>();
        services.AddScoped<IPublicSiteSearchService, PublicSiteSearchService>();

        return services;
    }
}
