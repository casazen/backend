using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Integration;

public class OnboardingIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public OnboardingIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PostOnboarding_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/users/onboarding", new { rentalType = "ShortTerm" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostOnboarding_InvalidRentalType_Returns400()
    {
        using var client = _factory.CreateAuthenticatedClient(roles: string.Empty);
        var response = await client.PostAsJsonAsync("/api/users/onboarding", new { rentalType = "Invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostOnboarding_ShortTerm_ReturnsPropertyOwner()
    {
        await AssertOnboardingSuccess("ShortTerm", ["PropertyOwner"]);
    }

    [Fact]
    public async Task PostOnboarding_LongTerm_ReturnsLongTermLandlord()
    {
        await AssertOnboardingSuccess("LongTerm", ["LongTermLandlord"]);
    }

    [Fact]
    public async Task PostOnboarding_Both_ReturnsBothRoles()
    {
        await AssertOnboardingSuccess("Both", ["PropertyOwner", "LongTermLandlord"]);
    }

    private async Task AssertOnboardingSuccess(string rentalType, string[] expectedRoles)
    {
        var userId = $"auth0|onboarding-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);

        var response = await client.PostAsJsonAsync("/api/users/onboarding", new { rentalType });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var assigned = body.GetProperty("rolesAssigned").EnumerateArray().Select(e => e.GetString()).ToArray();
        foreach (var role in expectedRoles)
            Assert.Contains(role, assigned);

        var me = await client.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var profile = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(rentalType, profile.GetProperty("rentalType").GetString());
    }

    [Fact]
    public async Task PutOnboarding_UpdatesRentalType_Idempotent()
    {
        var userId = $"auth0|onboarding-put-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);

        var post = await client.PostAsJsonAsync("/api/users/onboarding", new { rentalType = "ShortTerm" });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var put = await client.PutAsJsonAsync("/api/users/onboarding", new { rentalType = "LongTerm" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        var assigned = body.GetProperty("rolesAssigned").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Single(assigned);
        Assert.Equal("LongTermLandlord", assigned[0]);
    }
}
