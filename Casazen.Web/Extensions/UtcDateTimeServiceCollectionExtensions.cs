using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casazen.Web.Extensions;

/// <summary>
/// FD-06: every <see cref="DateTime"/> entering the API is UTC, and "today" has a testable clock.
/// EF Core applies the same rule on write/read (<c>AppDbContext.ConfigureConventions</c>).
/// </summary>
public static class UtcDateTimeServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenUtcDateTimeHandling(this IServiceCollection services)
    {
        // Clock for the calendar "today" in Europe/Rome (RomeCalendar.TodayInRome).
        services.TryAddSingleton(TimeProvider.System);

        // JSON bodies.
        services.Configure<JsonOptions>(options =>
            options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()));

        // Query string, route and form values: replace MVC's DateTime binder in place, so body,
        // header and service bindings keep their precedence.
        services.PostConfigure<MvcOptions>(options =>
        {
            var providers = options.ModelBinderProviders;
            var index = providers.ToList().FindIndex(p => p is DateTimeModelBinderProvider);
            if (index >= 0)
            {
                providers[index] = new UtcDateTimeModelBinderProvider();
                return;
            }

            var simpleTypeIndex = providers.ToList().FindIndex(p => p is SimpleTypeModelBinderProvider);
            providers.Insert(simpleTypeIndex >= 0 ? simpleTypeIndex : 0, new UtcDateTimeModelBinderProvider());
        });

        return services;
    }
}
