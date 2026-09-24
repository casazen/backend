using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Stripe Connect onboarding over HTTP (spec CO-AC1..3). BK-09: Stripe failures other than a missing or revoked account
/// never unlink the account (A3-19), parallel clicks create one account, only the org's billing admin starts the
/// onboarding and the Stripe return pages are built by the server (A3-42).
/// </summary>
public class ConnectOnboardingIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    /// <summary><c>App:PublicSiteBaseUrl</c> of <see cref="CasazenWebApplicationFactory"/>.</summary>
    private const string PublicSite = "https://casazen-app.vercel.app";

    private readonly CasazenWebApplicationFactory _factory;
    private readonly FakeStripeConnectGateway _stripe;

    public ConnectOnboardingIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        _stripe = (FakeStripeConnectGateway)factory.Services.GetRequiredService<IStripeConnectGateway>();
        _stripe.Reset();
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
        Assert.Equal(1, _stripe.CreateAccountCallCount);
        // BK-09: the creation carries an idempotency key bound to the org.
        Assert.Equal(new[] { $"connect-account:{org.Id:N}" }, _stripe.IdempotencyKeys);

        var persisted = await GetOrgAsync(org.Id);
        Assert.Equal(accountId, persisted.StripeConnectedAccountId);
    }

    [Fact]
    public async Task AC2_OnboardingLink_BuildsReturnAndRefreshUrlsServerSide_IgnoringClientUrls()
    {
        var owner = $"auth0|connect-ac2-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        // A3-42: URLs sent by a client (old web app, forged request) are ignored.
        var payload = JsonSerializer.Serialize(new
        {
            returnUrl = "https://evil.example.com/phish?return=1",
            refreshUrl = "https://evil.example.com/phish?refresh=1",
        });

        var response = await client.PostAsync(
            "/api/connect/onboarding-link",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("https://connect.stripe.test/", doc.RootElement.GetProperty("url").GetString());
        Assert.Equal($"{PublicSite}/app/short-rent/settings/payments?stripe_return=1", _stripe.LastReturnUrl);
        Assert.Equal($"{PublicSite}/app/short-rent/settings/payments?stripe_refresh=1", _stripe.LastRefreshUrl);
    }

    [Fact]
    public async Task AC2_OnboardingLink_WithoutBody_ReturnsUrl()
    {
        var owner = $"auth0|connect-ac2b-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PostAsync("/api/connect/onboarding-link", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{PublicSite}/app/short-rent/settings/payments?stripe_return=1", _stripe.LastReturnUrl);
    }

    [Fact]
    public async Task AC3_GetStatus_ReturnsCapabilityFlags()
    {
        var owner = $"auth0|connect-ac3-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");
        await PostAccountAsync(client);

        _stripe.NextSnapshot = new ConnectAccountSnapshot(
            _stripe.LastAccountId!,
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

    [Fact]
    public async Task CreateAccount_GetAccountRateLimited429_Returns503AndKeepsAccount()
    {
        var owner = $"auth0|connect-429-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        const string verified = "acct_verified_429";
        await LinkAccountAsync(org.Id, verified, chargesEnabled: true);
        _stripe.FailGetAccount(verified, HttpStatusCode.TooManyRequests, "rate_limit");
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await PostAccountAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("stripe_connect_unavailable", await ReadCodeAsync(response));
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(0, _stripe.CreateAccountCallCount);
        var persisted = await GetOrgAsync(org.Id);
        Assert.Equal(verified, persisted.StripeConnectedAccountId);
        Assert.True(persisted.ConnectChargesEnabled);
        Assert.True(persisted.ConnectPayoutsEnabled);
    }

    [Fact]
    public async Task OnboardingLink_InvalidPlatformKey_Returns503NotConfiguredAndKeepsAccount()
    {
        var owner = $"auth0|connect-key-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        const string verified = "acct_verified_key";
        await LinkAccountAsync(org.Id, verified, chargesEnabled: true);
        _stripe.FailGetAccount(verified, HttpStatusCode.Unauthorized, null);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PostAsync("/api/connect/onboarding-link", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("stripe_connect_not_configured", await ReadCodeAsync(response));
        Assert.Equal(0, _stripe.CreateAccountCallCount);
        Assert.Equal(verified, (await GetOrgAsync(org.Id)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task GetStatus_StripeUnavailable_Returns503AndKeepsCapabilities()
    {
        var owner = $"auth0|connect-status-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        const string verified = "acct_verified_status";
        await LinkAccountAsync(org.Id, verified, chargesEnabled: true);
        _stripe.FailGetAccount(verified, HttpStatusCode.ServiceUnavailable, null);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/connect/status?refresh=true");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("stripe_connect_unavailable", await ReadCodeAsync(response));
        var persisted = await GetOrgAsync(org.Id);
        Assert.Equal(verified, persisted.StripeConnectedAccountId);
        Assert.True(persisted.ConnectChargesEnabled);
    }

    [Fact]
    public async Task CreateAccount_AccountResourceMissing_ReplacesAccountWithNewOne()
    {
        var owner = $"auth0|connect-missing-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        const string deleted = "acct_deleted_on_stripe";
        await LinkAccountAsync(org.Id, deleted, chargesEnabled: true);
        _stripe.FailGetAccount(deleted, HttpStatusCode.NotFound, "resource_missing");
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await PostAccountAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var newAccount = doc.RootElement.GetProperty("connectedAccountId").GetString();
        Assert.Equal(_stripe.LastAccountId, newAccount);
        Assert.NotEqual(deleted, newAccount);
        Assert.Equal(1, _stripe.CreateAccountCallCount);
        Assert.Equal(new[] { $"connect-account:{org.Id:N}:replaces:{deleted}" }, _stripe.IdempotencyKeys);
        var persisted = await GetOrgAsync(org.Id);
        Assert.Equal(newAccount, persisted.StripeConnectedAccountId);
        Assert.False(persisted.ConnectChargesEnabled);
    }

    [PostgresFact]
    public async Task CreateAccount_TwoParallelClicks_CreateOneAccount()
    {
        Assert.True(_factory.UsesPostgreSql);
        var owner = $"auth0|connect-parallel-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        // Keeps the first creation in flight long enough for the second click to arrive before its commit.
        _stripe.CreateDelay = TimeSpan.FromMilliseconds(400);
        var first = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");
        var second = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var responses = await Task.WhenAll(PostAccountAsync(first), PostAccountAsync(second));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = new List<string?>();
        foreach (var response in responses)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            ids.Add(doc.RootElement.GetProperty("connectedAccountId").GetString());
        }

        Assert.Equal(1, _stripe.CreateAccountCallCount);
        Assert.Single(ids.Distinct());
        Assert.Equal(ids[0], (await GetOrgAsync(org.Id)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task CreateAccount_CollaboratorNotBillingAdmin_Returns403AndCreatesNothing()
    {
        // A3-42: a collaborator of the org whose short-rent role has property.write (enough for the old policy) but who is
        // not the org's billing admin (JWT role Staff) may read the status, not start or restart the onboarding.
        var owner = $"auth0|connect-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        var collaborator = await SeedCollaboratorAsync(org.Id, "property.read", "property.write", "payment.read");
        var client = _factory.CreateAuthenticatedClient(userId: collaborator, roles: "Staff");

        var account = await PostAccountAsync(client);
        var link = await client.PostAsync("/api/connect/onboarding-link", null);
        var status = await client.GetAsync("/api/connect/status?refresh=false");

        Assert.Equal(HttpStatusCode.Forbidden, account.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, link.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(0, _stripe.CreateAccountCallCount);
        Assert.Null(_stripe.LastReturnUrl);
        Assert.Null((await GetOrgAsync(org.Id)).StripeConnectedAccountId);
    }

    private static async Task<HttpResponseMessage> PostAccountAsync(HttpClient client) =>
        await client.PostAsync("/api/connect/account", null);

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    private async Task LinkAccountAsync(Guid orgId, string accountId, bool chargesEnabled)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        org.StripeConnectedAccountId = accountId;
        org.ConnectChargesEnabled = chargesEnabled;
        org.ConnectPayoutsEnabled = chargesEnabled;
        org.ConnectDetailsSubmitted = chargesEnabled;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A collaborator of the org: onboarded user of the org with a short-rent DB membership whose custom role has only
    /// <paramref name="permissions"/>.
    /// </summary>
    private async Task<string> SeedCollaboratorAsync(Guid orgId, params string[] permissions)
    {
        var userId = $"auth0|connect-collaborator-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Host",
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());

        var role = new Role
        {
            Id = Random.Shared.Next(100_000, int.MaxValue),
            ContextKey = "short-rent",
            RoleKey = $"bk09_collaborator_{Guid.NewGuid():N}",
        };
        foreach (var permission in permissions)
            role.Permissions.Add(new RolePermission { PermissionKey = permission });

        db.Roles.Add(role);
        db.UserContextMemberships.Add(new UserContextMembership { UserId = userId, ContextKey = "short-rent", RoleId = role.Id });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<Casazen.Core.Entities.Org> GetOrgAsync(Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
    }
}

/// <summary>
/// Stripe Connect of the integration tests (singleton of <see cref="CasazenWebApplicationFactory"/>). Failures are the
/// exceptions the real gateway throws for the given Stripe answer (<see cref="StripeConnectGateway.ToConnectException"/>).
/// It does not deduplicate idempotency keys: one creation per call, so a missing lock shows up as two accounts.
/// </summary>
internal sealed class FakeStripeConnectGateway : IStripeConnectGateway
{
    private readonly ConcurrentDictionary<string, StripeConnectException> _getAccountFailures = new();
    private readonly ConcurrentQueue<string> _idempotencyKeys = new();
    private int _createAccountCallCount;

    public int CreateAccountCallCount => Volatile.Read(ref _createAccountCallCount);
    public string? LastAccountId { get; private set; }
    public ConnectAccountSnapshot? NextSnapshot { get; set; }
    public TimeSpan CreateDelay { get; set; }
    public string? LastReturnUrl { get; private set; }
    public string? LastRefreshUrl { get; private set; }
    public IReadOnlyList<string> IdempotencyKeys => _idempotencyKeys.ToList();

    public void Reset()
    {
        Interlocked.Exchange(ref _createAccountCallCount, 0);
        _getAccountFailures.Clear();
        _idempotencyKeys.Clear();
        LastAccountId = null;
        NextSnapshot = null;
        CreateDelay = TimeSpan.Zero;
        LastReturnUrl = null;
        LastRefreshUrl = null;
    }

    /// <summary><c>GET /v1/accounts/{accountId}</c> answers with this Stripe error.</summary>
    public void FailGetAccount(string accountId, HttpStatusCode status, string? code) =>
        _getAccountFailures[accountId] = StripeConnectGateway.ToConnectException(
            StripeConnectGatewayTests.StripeError(status, code),
            "read the connected account");

    public async Task<string> CreateExpressAccountAsync(
        string email,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var number = Interlocked.Increment(ref _createAccountCallCount);
        _idempotencyKeys.Enqueue(idempotencyKey);
        if (CreateDelay > TimeSpan.Zero)
            await Task.Delay(CreateDelay, cancellationToken);

        var accountId = $"acct_test_{Guid.NewGuid():N}_{number}";
        LastAccountId = accountId;
        return accountId;
    }

    public Task<ConnectAccountSnapshot> GetAccountAsync(string connectedAccountId, CancellationToken cancellationToken = default)
    {
        if (_getAccountFailures.TryGetValue(connectedAccountId, out var failure))
            return Task.FromException<ConnectAccountSnapshot>(failure);

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
        CancellationToken cancellationToken = default)
    {
        LastReturnUrl = returnUrl;
        LastRefreshUrl = refreshUrl;
        return Task.FromResult($"https://connect.stripe.test/onboard/{connectedAccountId}");
    }
}
