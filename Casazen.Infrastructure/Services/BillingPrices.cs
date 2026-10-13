using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Stripe Price ids of the plans (PL-11, A1-31): one per tier of <see cref="PlanCatalog"/>, read from
/// <c>Billing:Prices:&lt;Tier&gt;</c> (Railway <c>Billing__Prices__Starter</c>, <c>__Pro</c>, <c>__Scale</c>). Every
/// environment has its own ids (test mode on Staging, live mode on Production) and no id is written in code or in
/// <c>appsettings.json</c>. A tier without a valid id cannot be bought: <c>GET /api/billing/plans</c> reports it as not
/// purchasable and the checkout answers 422 <see cref="PlanUnavailableCode"/>. In Production, once the Stripe secret key
/// is set, a missing id stops the startup instead (<c>Casazen.Web.Configuration.BillingConfiguration</c>). Runbook:
/// <c>docs/runbooks/stripe.md</c> § Environments.
/// </summary>
public static class BillingPrices
{
    public const string SectionName = "Billing:Prices";

    /// <summary>422 of a checkout for a tier whose Price id is not configured in this environment.</summary>
    public const string PlanUnavailableCode = "billing_plan_unavailable";

    /// <summary>Resource key of the message of <see cref="PlanUnavailableCode"/>.</summary>
    public const string PlanUnavailableMessageKey = "BillingPlanUnavailable";

    /// <summary>Prefix of every Stripe Price id.</summary>
    private const string PriceIdPrefix = "price_";

    /// <summary>Railway variable of the Price id of a tier.</summary>
    public static string VariableName(PlanTier tier) => $"Billing__Prices__{tier}";

    /// <summary>
    /// Price id of the tier, or null when it is empty, a placeholder (<c>price_PLACEHOLDER_…</c>, <c>YOUR_…</c>) or not a
    /// Stripe Price id (<c>price_…</c>).
    /// </summary>
    public static string? Resolve(IConfiguration configuration, PlanTier tier)
    {
        var value = configuration[$"{SectionName}:{tier}"]?.Trim();
        return IsPriceId(value) ? value : null;
    }

    /// <summary>Tier of the catalogue whose configured Price id is <paramref name="priceId"/>, or null.</summary>
    public static PlanTier? TierOf(IConfiguration configuration, string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId))
            return null;

        foreach (var entry in PlanCatalog.All)
        {
            if (string.Equals(Resolve(configuration, entry.Tier), priceId, StringComparison.Ordinal))
                return entry.Tier;
        }

        return null;
    }

    /// <summary>
    /// What stops the plans from being sold: tiers of the catalogue without a valid Price id, and one id set on two tiers
    /// (a payment would grant the wrong plan). Names the variables, never the values.
    /// </summary>
    public static IReadOnlyList<string> GetProblems(IConfiguration configuration)
    {
        var problems = new List<string>();

        var missing = PlanCatalog.All
            .Where(e => Resolve(configuration, e.Tier) is null)
            .Select(e => VariableName(e.Tier))
            .ToList();
        if (missing.Count > 0)
        {
            problems.Add(
                $"missing or invalid Stripe Price id in {string.Join(", ", missing)} (a price_… id of the Stripe mode of " +
                "this environment).");
        }

        var shared = PlanCatalog.All
            .Select(e => (e.Tier, PriceId: Resolve(configuration, e.Tier)))
            .Where(p => p.PriceId is not null)
            .GroupBy(p => p.PriceId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" and ", g.Select(p => VariableName(p.Tier))));
        problems.AddRange(shared.Select(tiers => $"{tiers} have the same Stripe Price id: every plan needs its own price."));

        return problems;
    }

    private static bool IsPriceId(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.StartsWith(PriceIdPrefix, StringComparison.Ordinal)
        && value.Length > PriceIdPrefix.Length
        && !value.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("...", StringComparison.Ordinal)
        && !value.Contains('…')
        && !value.Any(char.IsWhiteSpace);
}
