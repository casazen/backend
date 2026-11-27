using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The options of the GDPR section, the marketing suppression list, the guest segmentation, the marketing studio and the campaigns.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class MarketingServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenMarketingServices(this IServiceCollection services)
    {
        services.AddOptions<Casazen.Core.Options.GdprOptions>()
            .BindConfiguration(Casazen.Core.Options.GdprOptions.SectionName);
        // NF-05: Marketing suppression list with blind index (SHA-256) for one-click unsubscribe (ADR-007-R18, R22).
        services.AddScoped<ISuppressionService, SuppressionService>();
        // NF-24: Marketing campaigns and guest segmentation (ADR-014).
        services.AddScoped<IConditionEvaluator, ConditionEvaluator>();
        services.AddHttpClient<MarketingOpenEvents>(client =>
        {
            client.BaseAddress = new Uri("https://tourism.api.opendatahub.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<MarketingAiQuota>();
        services.AddScoped<MarketingStudio>();
        services.AddScoped<ICampaignService, CampaignService>();

        return services;
    }
}
