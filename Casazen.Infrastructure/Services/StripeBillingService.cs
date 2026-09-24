using System.Security.Cryptography;
using System.Text;
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
    private const string SubscriptionMode = "subscription";
    private const string PlanTierMetadataKey = "planTier";

    public async Task<string> EnsureCustomerAsync(Org org, CancellationToken cancellationToken = default)
    {
        ConfigureStripeApiKey();

        if (!string.IsNullOrWhiteSpace(org.StripeCustomerId))
            return org.StripeCustomerId;

        var email = string.IsNullOrWhiteSpace(org.ContactEmail) ? null : org.ContactEmail;
        var service = new StripeCustomerService();
        var customer = await service.CreateAsync(new Stripe.CustomerCreateOptions
        {
            Email = email,
            Metadata = new Dictionary<string, string> { ["orgId"] = org.Id.ToString() },
        }, new Stripe.RequestOptions
        {
            // A retry after a failed save of the customer id gets the same customer back instead of a second one
            // (Stripe keeps idempotency keys for 24 hours). The e-mail is part of the key because Stripe refuses a
            // key reused with different parameters.
            IdempotencyKey = $"casazen-org-customer-{org.Id:N}-{ShortHash(email ?? string.Empty)}",
        }, cancellationToken);

        return customer.Id;
    }

    public async Task<StripeCheckoutSession> CreateCheckoutSessionAsync(
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
            [PlanTierMetadataKey] = planTier.ToString(),
        };

        var service = new SessionService();
        var session = await service.CreateAsync(new SessionCreateOptions
        {
            Customer = org.StripeCustomerId,
            Mode = SubscriptionMode,
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Metadata = metadata,
            SubscriptionData = new SessionSubscriptionDataOptions { Metadata = metadata },
        }, cancellationToken: cancellationToken);

        return new StripeCheckoutSession(
            session.Id,
            session.Url ?? throw new InvalidOperationException("Stripe checkout session URL missing"),
            planTier);
    }

    public async Task<IReadOnlyList<StripeCheckoutSession>> ListOpenCheckoutSessionsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        ConfigureStripeApiKey();

        var service = new SessionService();
        var sessions = new List<StripeCheckoutSession>();
        await foreach (var session in service.ListAutoPagingAsync(
                           new SessionListOptions { Customer = customerId, Status = "open", Limit = 100 },
                           cancellationToken: cancellationToken))
        {
            if (!string.Equals(session.Mode, SubscriptionMode, StringComparison.Ordinal) || string.IsNullOrEmpty(session.Url))
                continue;

            PlanTier? tier = session.Metadata is not null &&
                             session.Metadata.TryGetValue(PlanTierMetadataKey, out var raw) &&
                             PlanCatalog.TryParseTier(raw, out var parsed)
                ? parsed
                : null;
            sessions.Add(new StripeCheckoutSession(session.Id, session.Url, tier));
        }

        return sessions;
    }

    public async Task ExpireCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ConfigureStripeApiKey();
        await new SessionService().ExpireAsync(sessionId, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<StripeSubscriptionSummary>> ListSubscriptionsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        ConfigureStripeApiKey();

        var service = new Stripe.SubscriptionService();
        var subscriptions = new List<StripeSubscriptionSummary>();
        await foreach (var subscription in service.ListAutoPagingAsync(
                           new Stripe.SubscriptionListOptions { Customer = customerId, Status = "all", Limit = 100 },
                           cancellationToken: cancellationToken))
        {
            subscriptions.Add(new StripeSubscriptionSummary(subscription.Id, subscription.Status));
        }

        return subscriptions;
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

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private void ConfigureStripeApiKey()
    {
        var secretKey = configuration["Stripe:SecretKey"];
        if (!string.IsNullOrWhiteSpace(secretKey))
            StripeConfiguration.ApiKey = secretKey;
    }
}
