using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Stripe.Checkout;
using PlanTier = Casazen.Core.Entities.Enums.PlanTier;
using StripeCustomerService = Stripe.CustomerService;


namespace Casazen.Infrastructure.Services;

public class StripeBillingService(IConfiguration configuration) : IStripeBillingService
{
    private const string SubscriptionMode = "subscription";
    private const string PlanTierMetadataKey = "planTier";

    public async Task<string> EnsureCustomerAsync(Org org, CancellationToken cancellationToken = default)
    {

        if (!string.IsNullOrWhiteSpace(org.StripeCustomerId))
            return org.StripeCustomerId;

        var email = string.IsNullOrWhiteSpace(org.ContactEmail) ? null : org.ContactEmail;
        var service = new StripeCustomerService(CreateClient());
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

    public Task<StripeCheckoutSession> CreateCheckoutSessionAsync(Org org, PlanTier planTier, string successUrl, string cancelUrl, CancellationToken cancellationToken = default) =>
        CreateCheckoutCoreAsync(org, planTier, successUrl, cancelUrl, cancellationToken, null);

    public Task<StripeCheckoutSession> CreateCommercialCheckoutSessionAsync(Org org, PlanTier planTier, string successUrl, string cancelUrl, CommercialSignup selection, CancellationToken cancellationToken = default) =>
        CreateCheckoutCoreAsync(org, planTier, successUrl, cancelUrl, cancellationToken, selection);

    private async Task<StripeCheckoutSession> CreateCheckoutCoreAsync(Org org, PlanTier planTier, string successUrl, string cancelUrl, CancellationToken cancellationToken, CommercialSignup? selection)
    {

        // The controller refuses the tier first (PL-11); this guard keeps a missing id a 422, never a Stripe call.
        var priceId = BillingPrices.Resolve(configuration, planTier)
            ?? throw new DomainRuleException(BillingPrices.PlanUnavailableCode, BillingPrices.PlanUnavailableMessageKey);

        var metadata = new Dictionary<string, string>
        {
            ["orgId"] = org.Id.ToString(),
            [PlanTierMetadataKey] = planTier.ToString(),
            ["basePriceId"] = priceId,
        };

        if (selection is not null) metadata["commercialConfiguration"] = selection.Fingerprint;
        var options = BuildCommercialCheckoutOptions(org.StripeCustomerId, priceId, successUrl, cancelUrl, metadata, selection,
            code => CommercialPricing.Price(configuration, code, planTier));
        var service = new SessionService(CreateClient());
        var session = await service.CreateAsync(
            options,
            cancellationToken: cancellationToken);

        return new StripeCheckoutSession(
            session.Id,
            session.Url ?? throw new InvalidOperationException("Stripe checkout session URL missing"),
            planTier);
    }

    public static SessionCreateOptions BuildCommercialCheckoutOptions(string? customerId, string priceId,
        string successUrl, string cancelUrl, Dictionary<string, string> metadata, CommercialSignup? selection,
        Func<string, string?> addonPrice)
    {
        if (selection?.Validate() is not null) throw new ArgumentException("Invalid commercial selection", nameof(selection));
        var options = BuildCheckoutSessionOptions(customerId, priceId, successUrl, cancelUrl, metadata);
        options.LineItems[0].Quantity = selection?.Quantity ?? 1;
        PlanCatalog.TryParseTier(selection?.PlanTier, out var selectedTier);
        foreach (var code in selection?.Addons ?? [])
        {
            if (CommercialPricing.IsIncluded(selectedTier, code)) continue;
            var price = addonPrice(code)
                ?? throw new DomainRuleException("commercial_addon_unavailable", BillingPrices.PlanUnavailableMessageKey);
            options.LineItems.Add(new SessionLineItemOptions { Price = price, Quantity = selection!.Quantity });
        }
        return options;
    }

    /// <summary>
    /// Checkout Session of a plan with Stripe Tax (PL-13, A1-08). CasaZen applies no rate of its own: Stripe Tax computes
    /// the VAT from the customer's billing address and tax id and from the tax registrations of the Stripe account
    /// (Italian VAT, OSS, reverse charge for a non-domestic EU VAT id; fiscale.md S1-S4). Parameters (Stripe API
    /// reference in the Stripe.net 50.1.0 docs, API 2025-12-15.clover):
    /// <list type="bullet">
    ///   <item><c>automatic_tax[enabled]</c>: tax computed for the session and for the resulting subscription and
    ///   invoices, renewals included;</item>
    ///   <item><c>billing_address_collection=required</c>: the full billing address is always asked;</item>
    ///   <item><c>tax_id_collection[enabled]</c>: the customer can enter a VAT id, saved on the Stripe customer and
    ///   verified by Stripe (VIES);</item>
    ///   <item><c>customer_update[address|name]=auto</c>: the address and the (business) name collected are saved on
    ///   the existing customer, otherwise Stripe Tax would not use them for the renewals.</item>
    /// </list>
    /// </summary>
    public static SessionCreateOptions BuildCheckoutSessionOptions(
        string? customerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        Dictionary<string, string> metadata) => new()
        {
            Customer = customerId,
            Mode = SubscriptionMode,
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Metadata = metadata,
            SubscriptionData = new SessionSubscriptionDataOptions { Metadata = metadata },
            AutomaticTax = new SessionAutomaticTaxOptions { Enabled = true },
            BillingAddressCollection = "required",
            TaxIdCollection = new SessionTaxIdCollectionOptions { Enabled = true },
            CustomerUpdate = string.IsNullOrWhiteSpace(customerId)
            ? null
            : new SessionCustomerUpdateOptions { Address = "auto", Name = "auto" },
        };

    public async Task<IReadOnlyList<StripeCheckoutSession>> ListOpenCheckoutSessionsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {

        var service = new SessionService(CreateClient());
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
            // A pre-rotation session still quotes the old base tariff. Let the checkout service expire it.
            if (tier is { } quotedTier && configuration[$"Billing:CheckoutPrices:{quotedTier}"] is not null &&
                session.Metadata?.GetValueOrDefault("basePriceId") != BillingPrices.Resolve(configuration, quotedTier))
                tier = null;
            sessions.Add(new StripeCheckoutSession(session.Id, session.Url, tier,
                session.Metadata?.GetValueOrDefault("commercialConfiguration"), session.SuccessUrl));
        }

        return sessions;
    }

    public async Task ExpireCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await new SessionService(CreateClient()).ExpireAsync(sessionId, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<StripeSubscriptionSummary>> ListSubscriptionsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {

        var service = new Stripe.SubscriptionService(CreateClient());
        var subscriptions = new List<StripeSubscriptionSummary>();
        await foreach (var subscription in service.ListAutoPagingAsync(
                           new Stripe.SubscriptionListOptions { Customer = customerId, Status = "all", Limit = 100 },
                           cancellationToken: cancellationToken))
        {
            subscriptions.Add(new StripeSubscriptionSummary(subscription.Id, subscription.Status));
        }

        return subscriptions;
    }

    public async Task<string> CreatePortalSessionAsync(
        Org org,
        string returnUrl,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(org.StripeCustomerId))
            throw new InvalidOperationException("Org has no Stripe customer id");

        ArgumentException.ThrowIfNullOrWhiteSpace(returnUrl);

        var service = new Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = org.StripeCustomerId,
            ReturnUrl = returnUrl,
        }, cancellationToken: cancellationToken);

        return session.Url ?? throw new InvalidOperationException("Stripe portal session URL missing");
    }

    public async Task<IReadOnlyList<StripeTaxRateSummary>> GetTaxRatesAsync(
        IReadOnlyCollection<string> taxRateIds,
        CancellationToken cancellationToken = default)
    {

        var service = new Stripe.TaxRateService(CreateClient());
        var rates = new List<StripeTaxRateSummary>();
        foreach (var id in taxRateIds.Distinct(StringComparer.Ordinal))
        {
            var rate = await service.GetAsync(id, cancellationToken: cancellationToken);
            rates.Add(new StripeTaxRateSummary(rate.Id, rate.Country, rate.EffectivePercentage, rate.Jurisdiction));
        }

        return rates;
    }

    public async Task<IReadOnlyList<StripeCustomerTaxId>> ListCustomerTaxIdsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {

        var service = new Stripe.CustomerTaxIdService(CreateClient());
        var taxIds = new List<StripeCustomerTaxId>();
        await foreach (var taxId in service.ListAutoPagingAsync(
                           customerId,
                           new Stripe.CustomerTaxIdListOptions { Limit = 100 },
                           cancellationToken: cancellationToken))
        {
            taxIds.Add(new StripeCustomerTaxId(taxId.Type, taxId.Value, taxId.Verification?.Status));
        }

        return taxIds;
    }

    public PlanTier? MapPriceIdToTier(string? priceId) => BillingPrices.TierOf(configuration, priceId);

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private Stripe.StripeClient CreateClient() => new(configuration["Stripe:SecretKey"]);
}
