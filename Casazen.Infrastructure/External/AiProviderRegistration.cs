using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

public static class AiProviderRegistration
{
    /// <summary>
    /// The AI provider (section <c>Ai</c>, runbook <c>docs/runbooks/ai.md</c>). Registered once, from Program.cs.
    /// With an external provider active (<see cref="AiOptions.IsExternalProviderActive"/>) every call goes through the
    /// platform budget (<see cref="BudgetGuardedAiProvider"/>); otherwise the stub answers without any external call.
    /// </summary>
    public static IServiceCollection AddCasazenAiProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AiOptions.SectionName);
        services.Configure<AiOptions>(section);
        services.AddScoped<IAiBudgetGuard, PlatformAiBudgetGuard>();

        var options = section.Get<AiOptions>() ?? new AiOptions();
        if (options.IsExternalProviderActive())
        {
            services.AddScoped<DeepSeekAiProvider>();
            services.AddScoped<IAiProvider>(sp => new BudgetGuardedAiProvider(
                sp.GetRequiredService<DeepSeekAiProvider>(),
                sp.GetRequiredService<IAiBudgetGuard>(),
                options.MaxCompletionTokens,
                sp.GetRequiredService<ILogger<BudgetGuardedAiProvider>>()));
        }
        else if (string.Equals(options.Provider, AiOptions.DeepSeekProvider, StringComparison.OrdinalIgnoreCase))
        {
            // DeepSeek without an API key answers empty without any call: nothing to budget.
            services.AddScoped<IAiProvider, DeepSeekAiProvider>();
        }
        else
        {
            services.AddScoped<IAiProvider, StubAiProvider>();
        }

        services.AddHttpClient("DeepSeek", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IWebSearchClient, DeepSeekWebSearchClient>();
        services.AddScoped<IAiSupplierDiscoveryService, AiSupplierDiscoveryService>();

        return services;
    }
}
