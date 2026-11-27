using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casazen.Web.Extensions;

public static class UtcDateTimeServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenUtcDateTimeHandling(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.Configure<JsonOptions>(options =>
            options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()));

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
