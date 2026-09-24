using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
}
