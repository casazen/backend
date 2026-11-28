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
        services.Configure<AiCacheOptions>(section.GetSection("Cache"));
        services.AddSingleton<IAiResponseCache, AiResponseCache>();
        services.AddScoped<IAiBudgetGuard, PlatformAiBudgetGuard>();
        services.AddSingleton<IAiRuntimeSettings, AiRuntimeSettings>();
        services.AddScoped<DeepSeekAiProvider>();
        services.AddScoped<StubAiProvider>();
        services.AddScoped<IAiProvider, SwitchingAiProvider>();
        // No document reader of the check-in here: since E70/F02/US02/T01 (NF-197, ADR-013-R21) the identity document is read in the process only
        // (LocalCheckInDocumentReader, registered with the self-hosted identity verification), never by an AI provider.

        services.AddHttpClient("DeepSeek", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(DeepSeekAiProvider.LongCallHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(90));
        services.AddScoped<IWebSearchClient, DeepSeekWebSearchClient>();
        services.AddScoped<IAiSupplierDiscoveryService, AiSupplierDiscoveryService>();

        return services;
    }
}
