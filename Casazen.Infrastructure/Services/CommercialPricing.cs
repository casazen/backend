using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

public static class CommercialPricing
{
    public static bool IsAvailable(IConfiguration configuration, string code)
    {
        var flag = code switch
        {
            "channels" => "OctorateConnector",
            "messages-ai" => "MessageAssistant",
            "fiscal" => "FiscalDocumentIssuance",
            "crm" => "CrmMarketing",
            "identification" => "CheckInIdentityVerification",
            _ => null,
        };
        return Price(configuration, code) is not null &&
            (flag is null || configuration.GetValue<bool>($"Features:{flag}"));
    }

    public static string? Price(IConfiguration configuration, string code)
    {
        var value = configuration[$"Billing:Addons:{code}:PriceId"]?.Trim();
        return value is not null && value.StartsWith("price_", StringComparison.Ordinal) && value.Length > 6 &&
            !value.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) && !value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase) && !value.Any(char.IsWhiteSpace) ? value : null;
    }
}
