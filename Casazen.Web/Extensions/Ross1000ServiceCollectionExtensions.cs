using Casazen.Infrastructure.Ross1000;

namespace Casazen.Web.Extensions;

public static class Ross1000ServiceCollectionExtensions
{
    public static IServiceCollection AddRoss1000Direct(this IServiceCollection services)
    {
        services.AddScoped<Ross1000DayService>();
        services.AddHttpClient<Ross1000Client>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.MaxResponseContentBufferSize = 4_000_000;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        return services;
    }
}
