using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

public class ConnectOnboardingIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public ConnectOnboardingIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeConnectGateway.Reset();
    }

    [Fact]
    public async Task AC1_CreateAccount_IsIdempotent_AndPersistsConnectedAccountId()
    {
        var owner = $"auth0|connect-ac1-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var first = await PostAccountAsync(client);
        var second = await PostAccountAsync(client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var doc1 = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var doc2 = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var accountId = doc1.RootElement.GetProperty("connectedAccountId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accountId));
        Assert.Equal(accountId, doc2.RootElement.GetProperty("connectedAccountId").GetString());
        Assert.Equal(1, FakeStripeConnectGateway.CreateAccountCallCount);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Orgs.FindAsync(org.Id);
        Assert.Equal(accountId, persisted!.StripeConnectedAccountId);
    }

    [Fact]
    public async Task AC2_OnboardingLink_ReturnsUrl()
    {
        var owner = $"auth0|connect-ac2-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");
        await PostAccountAsync(client);

        var payload = JsonSerializer.Serialize(new
        {
            returnUrl = "https://app.example.com/settings/payments?return=1",
            refreshUrl = "https://app.example.com/settings/payments?refresh=1",
        });

        var response = await client.PostAsync(
            "/api/connect/onboarding-link",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("https://connect.stripe.test/", doc.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public async Task AC3_GetStatus_ReturnsCapabilityFlags()
    {
        var owner = $"auth0|connect-ac3-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");
        await PostAccountAsync(client);

        FakeStripeConnectGateway.NextSnapshot = new ConnectAccountSnapshot(
            FakeStripeConnectGateway.LastAccountId!,
            ChargesEnabled: true,
            PayoutsEnabled: true,
            DetailsSubmitted: true,
            RequirementsDue: []);

        var response = await client.GetAsync("/api/connect/status?refresh=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("chargesEnabled").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("payoutsEnabled").GetBoolean());
    }

    private static async Task<HttpResponseMessage> PostAccountAsync(HttpClient client) =>
        await client.PostAsync("/api/connect/account", null);
}

internal sealed class FakeStripeConnectGateway : IStripeConnectGateway
{
    public static int CreateAccountCallCount { get; private set; }
    public static string? LastAccountId { get; private set; }
    public static ConnectAccountSnapshot? NextSnapshot { get; set; }

    public static void Reset()
    {
        CreateAccountCallCount = 0;
        LastAccountId = null;
        NextSnapshot = null;
    }

    public Task<string> CreateExpressAccountAsync(string email, CancellationToken cancellationToken = default)
    {
        CreateAccountCallCount++;
        LastAccountId = $"acct_test_{CreateAccountCallCount}";
        return Task.FromResult(LastAccountId);
    }

    public Task<ConnectAccountSnapshot> GetAccountAsync(string connectedAccountId, CancellationToken cancellationToken = default)
    {
        if (NextSnapshot is not null)
            return Task.FromResult(NextSnapshot);

        return Task.FromResult(new ConnectAccountSnapshot(
            connectedAccountId,
            ChargesEnabled: false,
            PayoutsEnabled: false,
            DetailsSubmitted: false,
            RequirementsDue: ["individual.verification.document"]));
    }

    public Task<string> CreateAccountOnboardingLinkAsync(
        string connectedAccountId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult($"https://connect.stripe.test/onboard/{connectedAccountId}");
}

public static class ConnectOnboardingWebApplicationFactoryExtensions
{
    public static CasazenWebApplicationFactory WithFakeStripeConnect(this CasazenWebApplicationFactory factory)
    {
        // Registered via partial factory hook — see factory ConfigureTestServices patch below if needed.
        return factory;
    }
}
