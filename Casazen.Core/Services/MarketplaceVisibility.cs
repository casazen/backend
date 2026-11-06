using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who may see a partner of the marketplace (E09/F01/US01/T03, NF-54, NF-59): the one rule that every endpoint of the marketplace applies,
/// pure, with no database and no clock. A hidden partner, or one that the plan or the country of the org does not allow, is never returned,
/// not as a card and not as a detail (a detail asked by its code is a 404, as for a code that does not exist).
/// </summary>
public static class MarketplaceVisibility
{
    /// <summary>
    /// True when <paramref name="partner"/> may be shown to an org on <paramref name="planTier"/> with <paramref name="billingCountry"/>:
    /// it is visible at all (<see cref="MarketplacePartner.IsVisible"/>); its plans are empty (every plan) or list the org's; its countries
    /// are empty (every country) or list the org's (<c>Org.BillingCountry</c>, case-insensitive). An org with no country sees only the
    /// partners with no country restriction (fail closed: a restriction is never lifted for an org whose country is not known).
    /// </summary>
    public static bool IsVisible(MarketplacePartner partner, PlanTier planTier, string? billingCountry)
    {
        ArgumentNullException.ThrowIfNull(partner);

        if (!partner.IsVisible)
            return false;

        if (partner.VisiblePlanTiers is { Length: > 0 } plans && Array.IndexOf(plans, planTier) < 0)
            return false;

        if (partner.VisibleCountries is { Count: > 0 } countries)
        {
            var country = billingCountry?.Trim();
            if (string.IsNullOrEmpty(country))
                return false;

            return countries.Any(listed => string.Equals(listed?.Trim(), country, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}
