using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Stripe.Checkout;
using PlanTier = Casazen.Core.Entities.Enums.PlanTier;
using StripeCustomerService = Stripe.CustomerService;
using StripeConfiguration = Stripe.StripeConfiguration;

namespace Casazen.Infrastructure.Services;

public class StripeBillingService(IConfiguration configuration) : IStripeBillingService
{
    public async Task<string> EnsureCustomerAsync(Org org, CancellationToken cancellationToken = default)
    {
        ConfigureStripeApiKey();

        if (!string.IsNullOrWhiteSpace(org.StripeCustomerId))
            return org.StripeCustomerId;

        var service = new StripeCustomerService();
        var customer = await service.CreateAsync(new Stripe.CustomerCreateOptions
        {
            Email = string.IsNullOrWhiteSpace(org.ContactEmail) ? null : org.ContactEmail,
            Metadata = new Dictionary<string, string> { ["orgId"] = org.Id.ToString() },
        }, cancellationToken: cancellationToken);

        return customer.Id;
    }

    public async Task<string> CreateCheckoutSessionAsync(
        Org org,
        PlanTier planTier,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        ConfigureStripeApiKey();

        var priceId = configuration[$"Billing:Prices:{planTier}"]
            ?? throw new InvalidOperationException($"Billing price not configured for tier {planTier}");

        var metadata = new Dictionary<string, string>
        {
            ["orgId"] = org.Id.ToString(),
            ["planTier"] = planTier.ToString(),
        };

        var service = new SessionService();
        var session = await service.CreateAsync(new SessionCreateOptions
        {
            Customer = org.StripeCustomerId,
            Mode = "subscription",
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Metadata = metadata,
            SubscriptionData = new SessionSubscriptionDataOptions { Metadata = metadata },
        }, cancellationToken: cancellationToken);

        return session.Url ?? throw new InvalidOperationException("Stripe checkout session URL missing");
    }

    public async Task<string> CreatePortalSessionAsync(Org org, CancellationToken cancellationToken = default)
    {
        ConfigureStripeApiKey();

        if (string.IsNullOrWhiteSpace(org.StripeCustomerId))
            throw new InvalidOperationException("Org has no Stripe customer id");

        var returnUrl = configuration["Billing:PortalReturnUrl"]
            ?? "https://app.casazen.app/settings/billing";

        var service = new Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = org.StripeCustomerId,
            ReturnUrl = returnUrl,
        }, cancellationToken: cancellationToken);

        return session.Url ?? throw new InvalidOperationException("Stripe portal session URL missing");
    }

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

    private void ConfigureStripeApiKey()
    {
        var secretKey = configuration["Stripe:SecretKey"];
        if (!string.IsNullOrWhiteSpace(secretKey))
            StripeConfiguration.ApiKey = secretKey;
    }
}
