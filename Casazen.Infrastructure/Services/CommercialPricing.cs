using Casazen.Core.Entities.Enums;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

public static class CommercialPricing
{
    // docs/business-plan/LISTINO.md §4. An owner activates these modules within the base subscription.
    public static bool IsIncluded(PlanTier tier, string code) =>
        (tier is PlanTier.Pro or PlanTier.Scale && code is "channels" or "direct-booking" or "compliance" or "operations") ||
        (tier == PlanTier.Scale && code == "messages-ai");

    public static bool IsAvailable(IConfiguration configuration, string code, PlanTier? tier = null) =>
        IsEnabled(configuration, code) && (tier.HasValue && IsIncluded(tier.Value, code) || Price(configuration, code, tier) is not null);

    public static bool IsEnabled(IConfiguration configuration, string code)
    {
        var flag = code switch
        {
            "ai-agents" => "Routines",
            "channels" => "OctorateConnector",
            "messages-ai" => "MessageAssistant",
            "fiscal" => "FiscalDocumentIssuance",
            "crm" => "CrmMarketing",
            "identification" => "CheckInIdentityVerification",
            _ => null,
        };
        return flag is null || configuration.GetValue<bool>($"Features:{flag}");
    }

    public static IConfigurationSection Offer(IConfiguration configuration, string code, PlanTier? tier = null)
    {
        var owner = configuration.GetSection($"Billing:OwnerAddons:{code}");
        return tier is PlanTier.Pro or PlanTier.Scale && owner.Exists() ? owner : configuration.GetSection($"Billing:Addons:{code}");
    }

    public static string? Price(IConfiguration configuration, string code, PlanTier? tier = null)
    {
        var value = Offer(configuration, code, tier)["PriceId"]?.Trim();
        return value is not null && value.StartsWith("price_", StringComparison.Ordinal) && value.Length > 6 &&
            !value.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) && !value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) && !value.Any(char.IsWhiteSpace) ? value : null;
    }
}
