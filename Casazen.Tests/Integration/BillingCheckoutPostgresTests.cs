using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Stripe;
using PlanTier = Casazen.Core.Entities.Enums.PlanTier;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// <c>POST /api/billing/checkout-session</c> never leads to a second subscription for the same org (A1-10), on real
/// PostgreSQL (per-org advisory lock) with an in-memory Stripe (<see cref="FakeStripeBillingService"/>). Each test
/// uses its own billing admin, org and Stripe customer.
/// </summary>
public class BillingCheckoutPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string AlreadySubscribed = "already_subscribed";

    private FakeStripeBillingService StripeFake =>
        (FakeStripeBillingService)factory.Services.GetRequiredService<IStripeBillingService>();

    [PostgresFact]
    public async Task CreateCheckoutSession_RepeatedAfterSubscriptionStarted_Returns409WithoutSecondSubscription()
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;
        var customerId = FakeStripeBillingService.CustomerIdFor(org.Id);

        Assert.Equal(HttpStatusCode.OK, (await StartCheckoutAsync(client, "Pro")).StatusCode);
        var subscriptionId = StripeFake.CompleteSession(Assert.Single(StripeFake.SessionsOf(customerId)).Id);
        await HandleWebhookAsync(StripeTestEvents.Subscription(
            "customer.subscription.created", subscriptionId, "active", org.Id, customerId, "price_test_pro"));

        var repeated = await StartCheckoutAsync(client, "Scale");

        await AssertProblemAsync(repeated, HttpStatusCode.Conflict, AlreadySubscribed);
        Assert.Single(StripeFake.SessionsOf(customerId));
        Assert.Equal(1, StripeFake.LiveSubscriptionCount(customerId));
        var subscription = await client.GetFromJsonAsync<JsonElement>("/api/billing/subscription");
        Assert.Equal("active", subscription.GetProperty("status").GetString());
        Assert.Equal("Pro", subscription.GetProperty("planTier").GetString());
    }

    [PostgresFact]
    public async Task CreateCheckoutSession_PaidInAnotherTabBeforeWebhook_Returns409()
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;
        var customerId = FakeStripeBillingService.CustomerIdFor(org.Id);

        Assert.Equal(HttpStatusCode.OK, (await StartCheckoutAsync(client, "Pro")).StatusCode);
        // Paid on Stripe; the customer.subscription.created webhook has not been processed yet.
        StripeFake.CompleteSession(Assert.Single(StripeFake.SessionsOf(customerId)).Id);

        var repeated = await StartCheckoutAsync(client, "Pro");

        await AssertProblemAsync(repeated, HttpStatusCode.Conflict, AlreadySubscribed);
        Assert.Single(StripeFake.SessionsOf(customerId));
        Assert.Equal(1, StripeFake.LiveSubscriptionCount(customerId));
    }

    [PostgresFact]
    public async Task CreateCheckoutSession_DoubleClick_ReturnsOneSessionAndOneCustomer()
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;
        var customerId = FakeStripeBillingService.CustomerIdFor(org.Id);
        var customersBefore = StripeFake.CustomersCreated;

        var responses = await Task.WhenAll(StartCheckoutAsync(client, "Pro"), StartCheckoutAsync(client, "Pro"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var urls = await Task.WhenAll(responses.Select(CheckoutUrlAsync));
        Assert.Single(urls.Distinct());
        var session = Assert.Single(StripeFake.SessionsOf(customerId));
        Assert.Equal("open", session.Status);
        Assert.Equal(1, StripeFake.CustomersCreated - customersBefore);
    }

    [PostgresFact]
    public async Task CreateCheckoutSession_OtherTierWhileSessionOpen_ExpiresPreviousSession()
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;
        var customerId = FakeStripeBillingService.CustomerIdFor(org.Id);

        var proUrl = await CheckoutUrlAsync(await StartCheckoutAsync(client, "Pro"));
        var scaleUrl = await CheckoutUrlAsync(await StartCheckoutAsync(client, "Scale"));
        var scaleAgainUrl = await CheckoutUrlAsync(await StartCheckoutAsync(client, "Scale"));

        Assert.NotEqual(proUrl, scaleUrl);
        Assert.Equal(scaleUrl, scaleAgainUrl);
        var sessions = StripeFake.SessionsOf(customerId);
        Assert.Equal(2, sessions.Count);
        // Only one session can still be paid.
        Assert.Equal("expired", Assert.Single(sessions, s => s.PlanTier == PlanTier.Pro).Status);
        Assert.Equal("open", Assert.Single(sessions, s => s.PlanTier == PlanTier.Scale).Status);
    }

    [PostgresFact]
    public async Task CreateCheckoutSession_IncompleteSubscription_Returns409AndGrantsNoPaidPlan()
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;
        var customerId = FakeStripeBillingService.CustomerIdFor(org.Id);

        Assert.Equal(HttpStatusCode.OK, (await StartCheckoutAsync(client, "Scale")).StatusCode);
        var subscriptionId = StripeFake.CompleteSession(Assert.Single(StripeFake.SessionsOf(customerId)).Id, "incomplete");
        await HandleWebhookAsync(StripeTestEvents.Subscription(
            "customer.subscription.created", subscriptionId, "incomplete", org.Id, customerId, "price_test_scale"));

        var subscription = await client.GetFromJsonAsync<JsonElement>("/api/billing/subscription");
        Assert.Equal("incomplete", subscription.GetProperty("status").GetString());
        Assert.Equal("Starter", subscription.GetProperty("planTier").GetString());
        var entitlement = await client.GetFromJsonAsync<JsonElement>("/api/orgs/me/entitlement");
        Assert.Equal("Starter", entitlement.GetProperty("planTier").GetString());
        Assert.False(entitlement.GetProperty("canUseCustomDomain").GetBoolean());

        // The pending first payment is completed from the portal, never with a second subscription.
        await AssertProblemAsync(await StartCheckoutAsync(client, "Scale"), HttpStatusCode.Conflict, AlreadySubscribed);
        Assert.Single(StripeFake.SessionsOf(customerId));
    }

    private async Task<(HttpClient Client, OrgEntity Org)> NewBillingAdminAsync()
    {
        var userId = $"auth0|pl10-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(userId);
        return (factory.CreateAuthenticatedClient(userId: userId, roles: "PropertyOwner"), org);
    }

    private static Task<HttpResponseMessage> StartCheckoutAsync(HttpClient client, string planTier) =>
        client.PostAsJsonAsync("/api/billing/checkout-session", new { planTier, billingCountry = "IT" });

    private static async Task<string> CheckoutUrlAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("checkoutUrl").GetString()!;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    private async Task HandleWebhookAsync(Event stripeEvent)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>()
            .HandleEventAsync(stripeEvent, WebhookSource.Platform);
    }
}
