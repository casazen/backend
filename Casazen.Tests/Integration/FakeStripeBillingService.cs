using System.Collections.Concurrent;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Integration;

/// <summary>
/// In-memory Stripe for the platform billing: customers, Checkout Sessions (open / expired) and subscriptions per
/// customer. Thread-safe, shared by the tests of one factory: each test works on its own org and customer.
/// </summary>
public sealed class FakeStripeBillingService(IConfiguration configuration) : IStripeBillingService
{
    private readonly ConcurrentDictionary<string, FakeSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _subscriptionsByCustomer = new();
    private int _customersCreated;

    /// <summary>Stripe customers created (not returned from the org).</summary>
    public int CustomersCreated => _customersCreated;

    public Task<string> EnsureCustomerAsync(OrgEntity org, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(org.StripeCustomerId))
            return Task.FromResult(org.StripeCustomerId);

        Interlocked.Increment(ref _customersCreated);
        return Task.FromResult(CustomerIdFor(org.Id));
    }

    public async Task<StripeCheckoutSession> CreateCheckoutSessionAsync(
        OrgEntity org,
        PlanTier planTier,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        // Widen the race window of parallel checkouts, as a real Stripe call would.
        await Task.Delay(50, cancellationToken);
        var id = $"cs_test_{Guid.NewGuid():N}";
        var url = $"https://checkout.stripe.test/session/{id}?tier={planTier}&success={Uri.EscapeDataString(successUrl)}";
        _sessions[id] = new FakeSession(id, url, org.StripeCustomerId ?? string.Empty, planTier);
        return new StripeCheckoutSession(id, url, planTier);
    }

    public Task<IReadOnlyList<StripeCheckoutSession>> ListOpenCheckoutSessionsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StripeCheckoutSession> open = _sessions.Values
            .Where(s => s.CustomerId == customerId && s.Status == "open")
            .Select(s => new StripeCheckoutSession(s.Id, s.Url, s.PlanTier))
            .ToList();
        return Task.FromResult(open);
    }

    public Task ExpireCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.Status != "open")
            throw new Stripe.StripeException($"Checkout session {sessionId} is not open");

        session.Status = "expired";
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StripeSubscriptionSummary>> ListSubscriptionsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StripeSubscriptionSummary> subscriptions = _subscriptionsByCustomer.TryGetValue(customerId, out var byId)
            ? byId.Select(kv => new StripeSubscriptionSummary(kv.Key, kv.Value)).ToList()
            : [];
        return Task.FromResult(subscriptions);
    }

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

    public static string CustomerIdFor(Guid orgId) => $"cus_test_{orgId:N}";

    /// <summary>Checkout Sessions ever created for the customer, whatever their status.</summary>
    public IReadOnlyList<(string Id, string Status, PlanTier PlanTier)> SessionsOf(string customerId) =>
        _sessions.Values
            .Where(s => s.CustomerId == customerId)
            .Select(s => (s.Id, s.Status, s.PlanTier))
            .ToList();

    /// <summary>
    /// Completes an open session as the customer would in Stripe Checkout: the session becomes <c>complete</c> and
    /// the customer gets a subscription with <paramref name="subscriptionStatus"/>. Returns the subscription id.
    /// </summary>
    public string CompleteSession(string sessionId, string subscriptionStatus = "active")
    {
        var session = _sessions[sessionId];
        if (session.Status != "open")
            throw new InvalidOperationException($"Checkout session {sessionId} is {session.Status}");

        session.Status = "complete";
        return AddSubscription(session.CustomerId, subscriptionStatus);
    }

    /// <summary>Adds a subscription to the customer on the Stripe side (the webhook has not run).</summary>
    public string AddSubscription(string customerId, string status)
    {
        var subscriptionId = $"sub_test_{Guid.NewGuid():N}";
        _subscriptionsByCustomer.GetOrAdd(customerId, _ => new ConcurrentDictionary<string, string>())[subscriptionId] = status;
        return subscriptionId;
    }

    /// <summary>Live subscriptions of the customer: every status but <c>canceled</c> and <c>incomplete_expired</c>.</summary>
    public int LiveSubscriptionCount(string customerId) =>
        _subscriptionsByCustomer.TryGetValue(customerId, out var byId)
            ? byId.Values.Count(BillingSubscriptionPolicy.BlocksNewCheckout)
            : 0;

    private sealed class FakeSession(string id, string url, string customerId, PlanTier planTier)
    {
        public string Id { get; } = id;
        public string Url { get; } = url;
        public string CustomerId { get; } = customerId;
        public PlanTier PlanTier { get; } = planTier;
        public string Status { get; set; } = "open";
    }
}
