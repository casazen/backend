using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Infrastructure.External;

public static class AiProviderRegistration
{
    /// <summary>
    /// The AI provider (section <c>Ai</c>, plus the admin override). Registered once, from Program.cs.
    /// <see cref="SwitchingAiProvider"/> reads the settings of each call: an active external provider goes through the
    /// platform budget (<see cref="BudgetGuardedAiProvider"/>); otherwise nothing is sent outside.
    /// </summary>
    public static IServiceCollection AddCasazenAiProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.SectionName);
        services.Configure<AiOptions>(section);
        // Bounded, expiring cache of the answers (A8-25): one per process, shared by every AI caller.
        services.Configure<AiCacheOptions>(section.GetSection("Cache"));
        services.AddSingleton<IAiResponseCache, AiResponseCache>();
        services.AddScoped<IAiBudgetGuard, PlatformAiBudgetGuard>();
        // The admin panel can switch the provider without a restart: each call reads the settings in effect.
        services.AddSingleton<IAiRuntimeSettings, AiRuntimeSettings>();
        services.AddScoped<DeepSeekAiProvider>();
        services.AddScoped<StubAiProvider>();
        services.AddScoped<IAiProvider, SwitchingAiProvider>();

        services.AddHttpClient("DeepSeek", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IWebSearchClient, DeepSeekWebSearchClient>();
        services.AddScoped<IAiSupplierDiscoveryService, AiSupplierDiscoveryService>();

        return services;
    }
}
