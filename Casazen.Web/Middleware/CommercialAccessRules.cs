using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.Middleware;

public enum CommercialAccessDecision
{
    Allowed,

    OnboardingRequired,

    ModuleRequired,
}

public static class CommercialAccessRules
{
    public const string OnboardingRequiredCode = "onboarding_required";

    public const string OnboardingRequiredMessageKey = "OnboardingConsentsRequired";

    public const string ModuleRequiredCode = "commercial_module_required";

    public static CommercialAccessDecision Evaluate(Org? org, string? module)
    {
        if (org?.CommercialSignupJson is null)
            return CommercialAccessDecision.Allowed;

        if (!org.CommercialPaymentConfirmed || org.SubscriptionStatus is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue))
            return CommercialAccessDecision.OnboardingRequired;

        if (module is not null)
        {
            var paid = JsonSerializer.Deserialize<string[]>(org.CommercialPaidAddonsJson ?? "[]") ?? [];
            if (!paid.Contains(module, StringComparer.Ordinal))
                return CommercialAccessDecision.ModuleRequired;
        }

        return CommercialAccessDecision.Allowed;
    }
}
