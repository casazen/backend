using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class BillingIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public BillingIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetPlans_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/billing/plans");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPlans_Authenticated_ReturnsCatalog()
    {
        await _factory.SeedOrgForOwnerAsync();
        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        var response = await client.GetAsync("/api/billing/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var plans = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, plans.ValueKind);
        Assert.True(plans.GetArrayLength() >= 3);
    }

    [Fact]
    public async Task CreateCheckoutSession_PropertyOwner_ReturnsCheckoutUrl()
    {
        await _factory.SeedOrgForOwnerAsync();
        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/billing/checkout-session", new
        {
            planTier = "Pro",
            billingCountry = "IT",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("https://checkout.stripe.test/", body.GetProperty("checkoutUrl").GetString());
    }

    [Theory]
    [InlineData("Platinum", "IT")]
    [InlineData("Pro", "ITA")]
    public async Task CreateCheckoutSession_InvalidRequest_Returns400ValidationProblem(string planTier, string billingCountry)
    {
        await _factory.SeedOrgForOwnerAsync();
        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/billing/checkout-session", new { planTier, billingCountry });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    /// <summary>Public URL of the web app configured by <see cref="CasazenWebApplicationFactory"/>.</summary>
    private const string PublicSite = "https://casazen-app.vercel.app";

    private const string PlanPage = PublicSite + "/app/short-rent/settings/plan";

    private FakeStripeBillingService StripeFake =>
        (FakeStripeBillingService)_factory.Services.GetRequiredService<IStripeBillingService>();

    [Fact]
    public async Task CreateCheckoutSession_WithoutReturnUrls_ReturnsToPlanPageOfConfiguredPublicSite()
    {
        // PL-11 (A1-31): no domain in code, the return pages are built from App:PublicSiteBaseUrl on a real route.
        using var client = await NewBillingAdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/billing/checkout-session", new { planTier = "Pro", billingCountry = "IT" });

        var query = await CheckoutQueryAsync(response);
        Assert.Equal($"{PlanPage}?checkout=success", query["success"]);
        Assert.Equal($"{PlanPage}?checkout=cancel", query["cancel"]);
    }

    [Fact]
    public async Task CreateCheckoutSession_ReturnUrlsOnPublicSite_AreSentToStripe()
    {
        using var client = await NewBillingAdminClientAsync();
        const string success = PublicSite + "/app/long-rent/settings/plan?checkout=success&session_id={CHECKOUT_SESSION_ID}";
        const string cancel = PublicSite + "/app/long-rent/settings/billing?checkout=cancel";

        var response = await client.PostAsJsonAsync(
            "/api/billing/checkout-session",
            new { planTier = "Scale", billingCountry = "IT", successUrl = success, cancelUrl = cancel });

        var query = await CheckoutQueryAsync(response);
        Assert.Equal(success, query["success"]);
        Assert.Equal(cancel, query["cancel"]);
    }

    [Theory]
    [InlineData("https://evil.example/phishing", null)]
    [InlineData(null, "https://casazen-app.vercel.app.evil.example/app")]
    [InlineData("http://casazen-app.vercel.app/app/short-rent/settings/plan", null)]
    [InlineData(null, "/app/short-rent/settings/plan")]
    [InlineData("https://casazen-app.vercel.app/app/billing", null)]
    [InlineData(null, "https://casazen-app.vercel.app/app/long-rent/leases")]
    public async Task CreateCheckoutSession_ReturnUrlOutsidePublicSite_Returns400WithoutCheckout(
        string? successUrl,
        string? cancelUrl)
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;

        var response = await client.PostAsJsonAsync(
            "/api/billing/checkout-session",
            new { planTier = "Pro", billingCountry = "IT", successUrl, cancelUrl });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.Empty(StripeFake.SessionsOf(FakeStripeBillingService.CustomerIdFor(org.Id)));
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orgs
            .AsNoTracking()
            .SingleAsync(o => o.Id == org.Id);
        Assert.Null(stored.BillingCountry);
        Assert.Null(stored.StripeCustomerId);
    }

    [Fact]
    public async Task CreatePortalSession_ReturnsToPlanPageOfConfiguredPublicSite()
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The org of this test's admin already has a Stripe customer.
            var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
            stored.StripeCustomerId = FakeStripeBillingService.CustomerIdFor(org.Id);
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync("/api/billing/portal-session", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var portalUrl = new Uri(body.GetProperty("portalUrl").GetString()!);
        Assert.Equal(PlanPage, System.Web.HttpUtility.ParseQueryString(portalUrl.Query)["return"]);
    }

    [Fact]
    public async Task CreateCheckoutSession_PlanWithoutPriceId_Returns422AndPlanIsNotPurchasable()
    {
        // PL-11 (A1-31): outside Production a plan without a Stripe Price id is disabled with an explicit error.
        await using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Prices:Scale"] = "price_PLACEHOLDER_scale" })));
        var userId = $"auth0|pl11-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(userId);
        using var client = AuthenticatedClient(factory, userId);

        var plans = await client.GetFromJsonAsync<JsonElement>("/api/billing/plans");
        var response = await client.PostAsJsonAsync("/api/billing/checkout-session", new { planTier = "Scale", billingCountry = "IT" });

        var byTier = plans.EnumerateArray().ToDictionary(p => p.GetProperty("tier").GetString()!);
        Assert.False(byTier["Scale"].GetProperty("purchasable").GetBoolean());
        Assert.Equal(string.Empty, byTier["Scale"].GetProperty("stripePriceId").GetString());
        Assert.True(byTier["Pro"].GetProperty("purchasable").GetBoolean());
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "billing_plan_unavailable");
        Assert.Empty(StripeFake.SessionsOf(FakeStripeBillingService.CustomerIdFor(org.Id)));
    }

    [Fact]
    public async Task CreateCheckoutSession_PublicSiteNotConfigured_Returns503()
    {
        await using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicSiteBaseUrl"] = "" })));
        var userId = $"auth0|pl11-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(userId);
        using var client = AuthenticatedClient(factory, userId);

        var response = await client.PostAsJsonAsync("/api/billing/checkout-session", new { planTier = "Pro", billingCountry = "IT" });

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "billing_return_url_not_configured");
    }

    [Fact]
    public async Task GetSubscription_ReturnsNoneForNewOrg()
    {
        await _factory.SeedOrgForOwnerAsync();
        using var client = _factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        var response = await client.GetAsync("/api/billing/subscription");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("none", body.GetProperty("status").GetString());
        Assert.Equal("Starter", body.GetProperty("planTier").GetString());
    }

    private async Task<HttpClient> NewBillingAdminClientAsync() => (await NewBillingAdminAsync()).Client;

    private async Task<(HttpClient Client, OrgEntity Org)> NewBillingAdminAsync()
    {
        var userId = $"auth0|pl11-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(userId);
        return (_factory.CreateAuthenticatedClient(userId: userId, roles: "PropertyOwner"), org);
    }

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "PropertyOwner");
        return client;
    }

    /// <summary>Query of the fake Stripe Checkout URL: the return pages the checkout was created with.</summary>
    private static async Task<System.Collections.Specialized.NameValueCollection> CheckoutQueryAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var checkoutUrl = new Uri(body.GetProperty("checkoutUrl").GetString()!);
        return System.Web.HttpUtility.ParseQueryString(checkoutUrl.Query);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }
}
