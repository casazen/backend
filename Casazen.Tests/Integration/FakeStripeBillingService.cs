using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Integration;

public sealed class FakeStripeBillingService(IConfiguration configuration) : IStripeBillingService
{
    public Task<string> EnsureCustomerAsync(OrgEntity org, CancellationToken cancellationToken = default)
    {
        var customerId = org.StripeCustomerId ?? $"cus_test_{org.Id:N}"[..18];
        return Task.FromResult(customerId);
    }

    public Task<string> CreateCheckoutSessionAsync(
        OrgEntity org,
        PlanTier planTier,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult($"https://checkout.stripe.test/session/{org.Id:N}/{planTier}?success={Uri.EscapeDataString(successUrl)}");

    public Task<string> CreatePortalSessionAsync(OrgEntity org, CancellationToken cancellationToken = default) =>
        Task.FromResult($"https://billing.stripe.test/portal/{org.StripeCustomerId ?? "cus_test"}");

    public PlanTier? MapPriceIdToTier(string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId))
            return null;

        foreach (PlanTier tier in Enum.GetValues<PlanTier>())
        {
            var configured = configuration[$"Billing:Prices:{tier}"];
            if (!string.IsNullOrEmpty(configured) &&
                string.Equals(configured, priceId, StringComparison.Ordinal))
            {
                return tier;
            }
        }

        return null;
    }
}
