using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.Middleware;

/// <summary>What the commercial gate decides for an org and a module.</summary>
public enum CommercialAccessDecision
{
    /// <summary>The org may go on: it did not sign up commercially, or its sign-up is paid, active and has the module.</summary>
    Allowed,

    /// <summary>A commercial sign-up whose first payment is not confirmed or whose subscription is not active: 403 <c>onboarding_required</c>.</summary>
    OnboardingRequired,

    /// <summary>A commercial sign-up that did not pay for the module: 403 <c>commercial_module_required</c>.</summary>
    ModuleRequired,
}

/// <summary>
/// The rule of the commercial gate, in one place: a new commercial signup cannot use the operations of a host before the subscription webhook confirms the payment,
/// and only the modules it paid for. <see cref="CommercialAccessMiddleware"/> applies it to the calls of a signed-in person, before authorization; the calls of a
/// connected plugin (scheme <c>SiteToken</c>) are authenticated by their policy, after that middleware, so <see cref="Infrastructure.SiteDomainAccessAttribute"/>
/// applies the same rule to them (S023, PLG-BE-SITEAPI).
/// </summary>
public static class CommercialAccessRules
{
    /// <summary>The <c>code</c> of the 403 for a commercial sign-up whose first payment is not confirmed or whose subscription is not active.</summary>
    public const string OnboardingRequiredCode = "onboarding_required";

    /// <summary>The resource key (<c>SharedResources</c>) of the explanation of <see cref="OnboardingRequiredCode"/>.</summary>
    public const string OnboardingRequiredMessageKey = "OnboardingConsentsRequired";

    /// <summary>The <c>code</c> of the 403 for a module that the commercial sign-up did not buy; the answer names the module next to it.</summary>
    public const string ModuleRequiredCode = "commercial_module_required";

    /// <summary>The decision for <paramref name="org"/> on <paramref name="module"/> (<c>null</c> for an operation that is no module of its own).</summary>
    public static CommercialAccessDecision Evaluate(Org? org, string? module)
    {
        // Only an org that signed up commercially is gated: the others keep the base experience.
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
