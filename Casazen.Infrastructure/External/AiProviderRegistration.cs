using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Infrastructure.External;

public static class AiProviderRegistration
{
    public static IServiceCollection AddCasazenAiProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));

        var provider = configuration[$"{AiOptions.SectionName}:Provider"] ?? "Stub";
        if (string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase))
            services.AddScoped<IAiProvider, DeepSeekAiProvider>();
        else
            services.AddScoped<IAiProvider, StubAiProvider>();

        services.AddHttpClient("DeepSeek", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IWebSearchClient, DeepSeekWebSearchClient>();
        services.AddScoped<IAiSupplierDiscoveryService, AiSupplierDiscoveryService>();

        return services;
    }
}
