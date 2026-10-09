using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-14 with <c>Features:SupplierOnlinePayments</c> off (the default): the supplier's payments routes answer 404 like a route
/// that does not exist, before authentication, and nothing reaches Stripe.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierPaymentsFlagOffTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private readonly FakeStripeConnectGateway _stripe;

    public SupplierPaymentsFlagOffTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        _stripe = (FakeStripeConnectGateway)factory.Services.GetRequiredService<IStripeConnectGateway>();
        _stripe.Reset();
    }

    [Theory]
    [InlineData("GET", "/api/supplier/payments/account")]
    [InlineData("GET", "/api/supplier/payments/account?refresh=true")]
    [InlineData("POST", "/api/supplier/payments/account")]
    [InlineData("POST", "/api/supplier/payments/onboarding-link")]
    [InlineData("POST", "/api/supplier/payments/dashboard-link")]
    public async Task Endpoints_FlagOffAsALinkedSupplier_Return404AndNothingReachesStripe(string method, string path)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var client = _factory.CreateAuthenticatedClient(userId, roles: "Supplier");

        var response = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request(method, path));

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
        Assert.Equal(0, _stripe.CreateAccountCallCount + _stripe.GetAccountCallCount + _stripe.OnboardingLinkCallCount + _stripe.LoginLinkCallCount);
        await using var scope = _factory.Services.CreateAsyncScope();
        var org = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
        Assert.Null(org.StripeConnectedAccountId);
    }

    [Theory]
    [InlineData("GET", "/api/supplier/payments/account")]
    [InlineData("POST", "/api/supplier/payments/onboarding-link")]
    [InlineData("POST", "/api/supplier/payments/dashboard-link")]
    public async Task Endpoints_FlagOffAnonymous_Return404NotUnauthorized(string method, string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request(method, path));

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
    }
}

/// <summary>
/// SP-14 on the real pipeline with the flag on: the supplier's Stripe Connect account <c>api/supplier/payments/*</c> (policy
/// <c>RequireSupplier</c>, the supplier org from its own link, never provisioned). The Stripe side is the fake gateway of the
/// host's onboarding tests, so what is proved is what CasaZen sends to Stripe and what it answers. Runs on PostgreSQL in CI and
/// on the in-memory fallback locally; what needs PostgreSQL (the lock) is the last test.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierPaymentsIntegrationTests : IClassFixture<SupplierFlagsEnabledFactory>
{
    private const string Base = "/api/supplier/payments";

    /// <summary><c>App:PublicSiteBaseUrl</c> of <see cref="CasazenWebApplicationFactory"/>.</summary>
    private const string PublicSite = "https://casazen-app.vercel.app";

    private readonly SupplierFlagsEnabledFactory _factory;
    private readonly FakeStripeConnectGateway _stripe;

    public SupplierPaymentsIntegrationTests(SupplierFlagsEnabledFactory factory)
    {
        _factory = factory;
        _stripe = (FakeStripeConnectGateway)factory.Services.GetRequiredService<IStripeConnectGateway>();
        _stripe.Reset();
    }

    // ─── Who may call it ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "account")]
    [InlineData("POST", "account")]
    [InlineData("POST", "onboarding-link")]
    [InlineData("POST", "dashboard-link")]
    public async Task Endpoints_Anonymous_Return401(string method, string route)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"{Base}/{route}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "account")]
    [InlineData("POST", "account")]
    [InlineData("POST", "onboarding-link")]
    [InlineData("POST", "dashboard-link")]
    public async Task Endpoints_SignedInHostWithoutTheSupplierRole_Return403AndNothingReachesStripe(string method, string route)
    {
        using var host = _factory.CreateAuthenticatedClient($"auth0|host-{Guid.NewGuid():N}", roles: "PropertyOwner");

        var response = await host.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"{Base}/{route}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _stripe.CreateAccountCallCount + _stripe.GetAccountCallCount + _stripe.OnboardingLinkCallCount);
    }

    [Theory]
    [InlineData("GET", "account")]
    [InlineData("POST", "account")]
    [InlineData("POST", "onboarding-link")]
    [InlineData("POST", "dashboard-link")]
    public async Task Endpoints_SupplierRoleWithoutALinkedSupplierOrg_Return404AndProvisionNothing(string method, string route)
    {
        var userId = $"auth0|unlinked-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: "Supplier");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"{Base}/{route}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await ReadAsync(response)).GetProperty("code").GetString());
        // The payments account never provisions a supplier org, nor a Stripe account for one.
        Assert.Equal(0, _stripe.CreateAccountCallCount);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AnyAsync(u => u.Id == userId));
    }

    // ─── The state ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccount_ANewSupplier_HasNoAccount_AndNothingReachesStripe()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync($"{Base}/account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.False(body.GetProperty("hasAccount").GetBoolean());
        Assert.False(body.GetProperty("chargesEnabled").GetBoolean());
        Assert.False(body.GetProperty("payoutsEnabled").GetBoolean());
        Assert.False(body.GetProperty("detailsSubmitted").GetBoolean());
        Assert.Equal(0, body.GetProperty("requirementsDue").GetArrayLength());
        Assert.False(body.GetProperty("canReceivePayments").GetBoolean());
        Assert.False(body.GetProperty("verified").GetBoolean());
        // Active profile, but no payments and no VAT number yet.
        Assert.Equal(["payments_not_enabled", "vat_number_missing"], Strings(body.GetProperty("verificationMissing")));
        // The Stripe account id and anything bank-related are not part of the answer.
        Assert.Equal(
            ["canReceivePayments", "chargesEnabled", "detailsSubmitted", "hasAccount", "payoutsEnabled", "requirementsDue", "verificationMissing", "verified"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(0, _stripe.CreateAccountCallCount + _stripe.GetAccountCallCount);
    }

    [Fact]
    public async Task GetAccount_ReadsTheDatabase_AndStripeOnlyOnAnExplicitRefresh()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        await LinkAccountAsync(orgId, "acct_sp14_state", chargesEnabled: false, payoutsEnabled: false);
        _stripe.NextSnapshot = new ConnectAccountSnapshot("acct_sp14_state", true, true, true, []);
        using var client = SupplierClient(userId);

        var cached = await ReadAsync(await client.GetAsync($"{Base}/account"));

        // Without refresh: what the webhooks stored, no call to Stripe.
        Assert.True(cached.GetProperty("hasAccount").GetBoolean());
        Assert.False(cached.GetProperty("canReceivePayments").GetBoolean());
        Assert.Equal(0, _stripe.GetAccountCallCount);

        var refreshed = await ReadAsync(await client.GetAsync($"{Base}/account?refresh=true"));

        Assert.True(refreshed.GetProperty("chargesEnabled").GetBoolean());
        Assert.True(refreshed.GetProperty("payoutsEnabled").GetBoolean());
        Assert.True(refreshed.GetProperty("detailsSubmitted").GetBoolean());
        Assert.True(refreshed.GetProperty("canReceivePayments").GetBoolean());
        Assert.Equal(1, _stripe.GetAccountCallCount);
        var stored = await GetOrgAsync(orgId);
        Assert.True(stored.ConnectChargesEnabled);
        Assert.True(stored.ConnectPayoutsEnabled);

        // The refresh was saved: the next plain read has it, still without Stripe.
        var later = await ReadAsync(await client.GetAsync($"{Base}/account"));
        Assert.True(later.GetProperty("canReceivePayments").GetBoolean());
        Assert.Equal(1, _stripe.GetAccountCallCount);
    }

    [Fact]
    public async Task GetAccount_WithAndWithoutChargesAndPayouts_ReportsEachCapability()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        await LinkAccountAsync(orgId, "acct_sp14_caps", chargesEnabled: false, payoutsEnabled: false);
        using var client = SupplierClient(userId);

        // Stripe reports charges but no payouts yet, with two requirements.
        _stripe.NextSnapshot = new ConnectAccountSnapshot(
            "acct_sp14_caps", true, false, true, ["external_account", "individual.verification.document"]);
        var chargesOnly = await ReadAsync(await client.GetAsync($"{Base}/account?refresh=true"));
        Assert.True(chargesOnly.GetProperty("chargesEnabled").GetBoolean());
        Assert.False(chargesOnly.GetProperty("payoutsEnabled").GetBoolean());
        Assert.False(chargesOnly.GetProperty("canReceivePayments").GetBoolean());
        Assert.Equal(["external_account", "individual.verification.document"], Strings(chargesOnly.GetProperty("requirementsDue")));

        // Then payouts too, nothing left to do.
        _stripe.NextSnapshot = new ConnectAccountSnapshot("acct_sp14_caps", true, true, true, []);
        var both = await ReadAsync(await client.GetAsync($"{Base}/account?refresh=true"));
        Assert.True(both.GetProperty("canReceivePayments").GetBoolean());
        Assert.Equal(0, both.GetProperty("requirementsDue").GetArrayLength());

        // And back to disabled (Stripe can disable an account again).
        _stripe.NextSnapshot = new ConnectAccountSnapshot("acct_sp14_caps", false, false, true, ["external_account"]);
        var disabled = await ReadAsync(await client.GetAsync($"{Base}/account?refresh=true"));
        Assert.False(disabled.GetProperty("canReceivePayments").GetBoolean());
    }

    [Fact]
    public async Task GetAccount_Verified_NeedsAnActiveProfile_PaymentsEnabledAndAVatNumber()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        await LinkAccountAsync(orgId, "acct_sp14_verified", chargesEnabled: true, payoutsEnabled: true);
        using var client = SupplierClient(userId);

        var withoutVat = await ReadAsync(await client.GetAsync($"{Base}/account"));
        Assert.False(withoutVat.GetProperty("verified").GetBoolean());
        Assert.Equal(["vat_number_missing"], Strings(withoutVat.GetProperty("verificationMissing")));

        await UpdateProfileAsync(orgId, p => p.VatNumber = "IT12345678901");
        var verified = await ReadAsync(await client.GetAsync($"{Base}/account"));
        Assert.True(verified.GetProperty("verified").GetBoolean());
        Assert.Equal(0, verified.GetProperty("verificationMissing").GetArrayLength());

        await UpdateProfileAsync(orgId, p => p.Status = SupplierStatus.Suspended);
        var suspended = await ReadAsync(await client.GetAsync($"{Base}/account"));
        Assert.False(suspended.GetProperty("verified").GetBoolean());
        Assert.Equal(["profile_not_active"], Strings(suspended.GetProperty("verificationMissing")));
        // The status is only read: the profile is exactly as the test left it.
        Assert.Equal(SupplierStatus.Suspended, (await GetProfileAsync(orgId)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit", HttpStatusCode.ServiceUnavailable, "stripe_connect_unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, HttpStatusCode.ServiceUnavailable, "stripe_connect_unavailable")]
    [InlineData(HttpStatusCode.Unauthorized, null, HttpStatusCode.ServiceUnavailable, "stripe_connect_not_configured")]
    [InlineData(HttpStatusCode.BadRequest, null, HttpStatusCode.BadGateway, "stripe_connect_failed")]
    public async Task GetAccount_RefreshWhenStripeFails_AnswersLikeTheHostOnboardingAndKeepsTheStoredState(
        HttpStatusCode stripeStatus,
        string? stripeCode,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        const string account = "acct_sp14_failing";
        await LinkAccountAsync(orgId, account, chargesEnabled: true, payoutsEnabled: true);
        _stripe.FailGetAccount(account, stripeStatus, stripeCode);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync($"{Base}/account?refresh=true");

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, (await ReadAsync(response)).GetProperty("code").GetString());
        if (expectedCode == "stripe_connect_unavailable")
            Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        var stored = await GetOrgAsync(orgId);
        Assert.Equal(account, stored.StripeConnectedAccountId);
        Assert.True(stored.ConnectChargesEnabled);
        Assert.True(stored.ConnectPayoutsEnabled);
    }

    // ─── The account and the onboarding link ─────────────────────────────────────

    [Fact]
    public async Task OnboardingLink_BuildsReturnAndRefreshUrlsOnTheSupplierSettingsPage_IgnoringClientUrls()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var client = SupplierClient(userId);
        // URLs sent by a client (old web app, forged request) are ignored: Stripe only comes back to the supplier console.
        var payload = JsonSerializer.Serialize(new
        {
            returnUrl = "https://evil.example.com/phish?return=1",
            refreshUrl = "https://evil.example.com/phish?refresh=1",
        });

        var response = await client.PostAsync($"{Base}/onboarding-link", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await GetOrgAsync(orgId);
        Assert.False(string.IsNullOrWhiteSpace(stored.StripeConnectedAccountId));
        Assert.Equal(
            $"https://connect.stripe.test/onboard/{stored.StripeConnectedAccountId}",
            (await ReadAsync(response)).GetProperty("url").GetString());
        Assert.Equal($"{PublicSite}/app/supplier/settings?stripe_return=1", _stripe.LastReturnUrl);
        Assert.Equal($"{PublicSite}/app/supplier/settings?stripe_refresh=1", _stripe.LastRefreshUrl);
        // A single-use credential: never cached.
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
    }

    [Fact]
    public async Task OnboardingLink_WithoutBody_ReturnsAUrl()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var client = SupplierClient(userId);

        var response = await client.PostAsync($"{Base}/onboarding-link", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("https://connect.stripe.test/onboard/", (await ReadAsync(response)).GetProperty("url").GetString());
    }

    [Fact]
    public async Task OnboardingLink_Repeated_CreatesTheAccountOnceWithTheOrgIdempotencyKey()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var client = SupplierClient(userId);

        var first = await client.PostAsync($"{Base}/onboarding-link", null);
        var second = await client.PostAsync($"{Base}/onboarding-link", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, _stripe.CreateAccountCallCount);
        Assert.Equal(2, _stripe.OnboardingLinkCallCount);
        // Same key as the host's onboarding: connect-account:{orgId}.
        Assert.Equal(new[] { $"connect-account:{orgId:N}" }, _stripe.IdempotencyKeys);
        Assert.Equal(
            (await ReadAsync(first)).GetProperty("url").GetString(),
            (await ReadAsync(second)).GetProperty("url").GetString());
    }

    [Fact]
    public async Task CreateAccount_IsIdempotent_AndReturnsTheStateWithoutALink()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var client = SupplierClient(userId);

        var first = await client.PostAsync($"{Base}/account", null);
        var second = await client.PostAsync($"{Base}/account", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var state = await ReadAsync(second);
        Assert.True(state.GetProperty("hasAccount").GetBoolean());
        Assert.False(state.GetProperty("canReceivePayments").GetBoolean());
        Assert.Equal(["individual.verification.document"], Strings(state.GetProperty("requirementsDue")));
        Assert.Equal(1, _stripe.CreateAccountCallCount);
        Assert.Equal(0, _stripe.OnboardingLinkCallCount);
        Assert.False(string.IsNullOrWhiteSpace((await GetOrgAsync(orgId)).StripeConnectedAccountId));
    }

    [Fact]
    public async Task OnboardingLink_AccountGoneOnStripe_ReplacesItWithTheReplacementKey()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        const string deleted = "acct_sp14_deleted";
        await LinkAccountAsync(orgId, deleted, chargesEnabled: true, payoutsEnabled: true);
        _stripe.FailGetAccount(deleted, HttpStatusCode.NotFound, "resource_missing");
        using var client = SupplierClient(userId);

        var response = await client.PostAsync($"{Base}/onboarding-link", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { $"connect-account:{orgId:N}:replaces:{deleted}" }, _stripe.IdempotencyKeys);
        var stored = await GetOrgAsync(orgId);
        Assert.NotEqual(deleted, stored.StripeConnectedAccountId);
        Assert.Equal(_stripe.LastAccountId, stored.StripeConnectedAccountId);
        Assert.False(stored.ConnectChargesEnabled);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit", HttpStatusCode.ServiceUnavailable, "stripe_connect_unavailable")]
    [InlineData(HttpStatusCode.Unauthorized, null, HttpStatusCode.ServiceUnavailable, "stripe_connect_not_configured")]
    [InlineData(HttpStatusCode.BadRequest, null, HttpStatusCode.BadGateway, "stripe_connect_failed")]
    public async Task OnboardingLink_StripeFailsOnTheLinkedAccount_AnswersLikeTheHostAndNeverUnlinksIt(
        HttpStatusCode stripeStatus,
        string? stripeCode,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        const string account = "acct_sp14_kept";
        await LinkAccountAsync(orgId, account, chargesEnabled: true, payoutsEnabled: true);
        _stripe.FailGetAccount(account, stripeStatus, stripeCode);
        using var client = SupplierClient(userId);

        var response = await client.PostAsync($"{Base}/onboarding-link", null);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, _stripe.CreateAccountCallCount);
        Assert.Equal(0, _stripe.OnboardingLinkCallCount);
        Assert.Equal(account, (await GetOrgAsync(orgId)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task OnboardingLink_PublicSiteNotConfigured_Returns503BeforeAnyStripeCall()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        // Possible only in Development/Testing: elsewhere the startup fails without App:PublicSiteBaseUrl.
        await using var unconfigured = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicSiteBaseUrl"] = "" })));
        var stripe = (FakeStripeConnectGateway)unconfigured.Services.GetRequiredService<IStripeConnectGateway>();
        using var client = unconfigured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Supplier");

        var response = await client.PostAsync($"{Base}/onboarding-link", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("connect_return_url_not_configured", (await ReadAsync(response)).GetProperty("code").GetString());
        // No account is created for a link that could not send the supplier back.
        Assert.Equal(0, stripe.CreateAccountCallCount + stripe.OnboardingLinkCallCount);
        Assert.Null((await GetOrgAsync(orgId)).StripeConnectedAccountId);
    }

    // ─── The dashboard link ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("it", "non è ancora pronto")]
    [InlineData("en", "not ready yet")]
    public async Task DashboardLink_WithoutAnAccount_Returns422NotReady_InTheCallersLanguage_AndCreatesNothing(
        string language,
        string expectedText)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var client = SupplierClient(userId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/dashboard-link");
        request.Headers.Add("Accept-Language", language);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("supplier_payments_not_ready", problem.GetProperty("code").GetString());
        Assert.Contains(expectedText, problem.GetProperty("detail").GetString());
        // No account is created to open its dashboard, and Stripe is not called.
        Assert.Equal(0, _stripe.CreateAccountCallCount + _stripe.LoginLinkCallCount + _stripe.GetAccountCallCount);
        Assert.Null((await GetOrgAsync(orgId)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task DashboardLink_WithAnAccount_ReturnsTheExpressLoginLink()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        const string account = "acct_sp14_dashboard";
        await LinkAccountAsync(orgId, account, chargesEnabled: true, payoutsEnabled: true);
        using var client = SupplierClient(userId);

        var response = await client.PostAsync($"{Base}/dashboard-link", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"https://connect.stripe.test/express/{account}", (await ReadAsync(response)).GetProperty("url").GetString());
        Assert.Equal(account, _stripe.LastLoginLinkAccountId);
        Assert.Equal(1, _stripe.LoginLinkCallCount);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task DashboardLink_StripeRefusesBecauseTheOnboardingIsIncomplete_Returns422NotReady()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        const string account = "acct_sp14_incomplete";
        await LinkAccountAsync(orgId, account, chargesEnabled: false, payoutsEnabled: false);
        _stripe.FailLoginLink(account, HttpStatusCode.BadRequest, null);
        using var client = SupplierClient(userId);

        var response = await client.PostAsync($"{Base}/dashboard-link", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("supplier_payments_not_ready", (await ReadAsync(response)).GetProperty("code").GetString());
        Assert.Equal(account, (await GetOrgAsync(orgId)).StripeConnectedAccountId);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit", HttpStatusCode.ServiceUnavailable, "stripe_connect_unavailable")]
    [InlineData(HttpStatusCode.Unauthorized, null, HttpStatusCode.ServiceUnavailable, "stripe_connect_not_configured")]
    [InlineData(HttpStatusCode.NotFound, "resource_missing", HttpStatusCode.Conflict, "stripe_connect_account_unavailable")]
    public async Task DashboardLink_OtherStripeFailures_AnswerLikeTheHostOnboarding(
        HttpStatusCode stripeStatus,
        string? stripeCode,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        const string account = "acct_sp14_dash_fail";
        await LinkAccountAsync(orgId, account, chargesEnabled: true, payoutsEnabled: true);
        _stripe.FailLoginLink(account, stripeStatus, stripeCode);
        using var client = SupplierClient(userId);

        var response = await client.PostAsync($"{Base}/dashboard-link", null);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, (await ReadAsync(response)).GetProperty("code").GetString());
        // A dashboard link never unlinks the account; the onboarding link is what replaces a gone one.
        Assert.Equal(account, (await GetOrgAsync(orgId)).StripeConnectedAccountId);
    }

    // ─── Isolation and the host's flow ───────────────────────────────────────────

    [Fact]
    public async Task TwoSuppliers_EachHasItsOwnAccount_AndNeverTheOthers()
    {
        var (aUser, aOrg) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        var (bUser, bOrg) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);

        await a.PostAsync($"{Base}/onboarding-link", null);
        var aAccount = (await GetOrgAsync(aOrg)).StripeConnectedAccountId;

        // B has nothing: its state is empty and its dashboard link is not A's account.
        var bState = await ReadAsync(await b.GetAsync($"{Base}/account"));
        Assert.False(bState.GetProperty("hasAccount").GetBoolean());
        var bDashboard = await b.PostAsync($"{Base}/dashboard-link", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bDashboard.StatusCode);
        Assert.Equal(0, _stripe.LoginLinkCallCount);

        await b.PostAsync($"{Base}/onboarding-link", null);
        var bAccount = (await GetOrgAsync(bOrg)).StripeConnectedAccountId;

        Assert.NotNull(aAccount);
        Assert.NotNull(bAccount);
        Assert.NotEqual(aAccount, bAccount);
        Assert.Equal(aAccount, (await GetOrgAsync(aOrg)).StripeConnectedAccountId);
        Assert.Equal(
            new[] { $"connect-account:{aOrg:N}", $"connect-account:{bOrg:N}" },
            _stripe.IdempotencyKeys);
    }

    [Fact]
    public async Task ADualRoleAccount_UsesItsSupplierOrgHere_AndItsHostOrgOnTheHostRoutes_WithTheirOwnReturnPages()
    {
        // A host that is also a supplier: User.OrgId is the host org, User.SupplierOrgId the supplier org.
        var ownerId = $"auth0|sp14-dual-{Guid.NewGuid():N}";
        var hostOrg = await _factory.SeedOrgForOwnerAsync(ownerId);
        Guid supplierOrgId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var email = $"sp14.dual.{Guid.NewGuid():N}@example.com";
            var supplierOrg = new OrgEntity
            {
                Name = "Fornitore doppio ruolo",
                Slug = $"supplier-{Guid.NewGuid():N}"[..30],
                DisplayName = "Fornitore doppio ruolo",
                ContactEmail = email,
                OrgType = OrgType.Supplier,
                PlanTier = PlanTier.Starter,
                IsActive = true,
            };
            db.Orgs.Add(supplierOrg);
            db.SupplierProfiles.Add(new SupplierProfile
            {
                OrgId = supplierOrg.Id,
                Email = email,
                LegalName = "Doppio Srl",
                Phone = "+39 06 040404",
                Status = SupplierStatus.Active,
            });
            var user = await db.Users.SingleAsync(u => u.Id == ownerId);
            user.SupplierOrgId = supplierOrg.Id;
            await db.SaveChangesAsync();
            supplierOrgId = supplierOrg.Id;
        }

        using var client = _factory.CreateAuthenticatedClient(ownerId, roles: "Supplier,PropertyOwner");

        var supplierLink = await client.PostAsync($"{Base}/onboarding-link", null);
        Assert.Equal(HttpStatusCode.OK, supplierLink.StatusCode);
        Assert.Equal($"{PublicSite}/app/supplier/settings?stripe_return=1", _stripe.LastReturnUrl);
        var supplierAccount = (await GetOrgAsync(supplierOrgId)).StripeConnectedAccountId;
        Assert.False(string.IsNullOrWhiteSpace(supplierAccount));
        Assert.Null((await GetOrgAsync(hostOrg.Id)).StripeConnectedAccountId);

        // The host's own routes are unchanged: they create the host org's account, with the host's return pages.
        var hostLink = await client.PostAsync("/api/connect/onboarding-link", null);
        Assert.Equal(HttpStatusCode.OK, hostLink.StatusCode);
        Assert.Equal($"{PublicSite}/app/short-rent/settings/payments?stripe_return=1", _stripe.LastReturnUrl);
        Assert.Equal($"{PublicSite}/app/short-rent/settings/payments?stripe_refresh=1", _stripe.LastRefreshUrl);
        var hostAccount = (await GetOrgAsync(hostOrg.Id)).StripeConnectedAccountId;
        Assert.False(string.IsNullOrWhiteSpace(hostAccount));
        Assert.NotEqual(supplierAccount, hostAccount);
        Assert.Equal(supplierAccount, (await GetOrgAsync(supplierOrgId)).StripeConnectedAccountId);
        Assert.Equal(2, _stripe.CreateAccountCallCount);
    }

    [Fact]
    public async Task HostRoutes_AreUnchanged_ASupplierOnlyAccountDoesNotReachThem()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        using var supplier = SupplierClient(userId);

        // The supplier role is not enough for the host's onboarding (OrgBillingAdmin), and the host has nothing of the supplier's.
        var response = await supplier.PostAsync("/api/connect/onboarding-link", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _stripe.CreateAccountCallCount);
    }

    // ─── PostgreSQL ──────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task OnboardingLink_TwoParallelRequests_CreateOneAccount()
    {
        Assert.True(_factory.UsesPostgreSql);
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        // Keeps the first creation in flight long enough for the second request to arrive before its commit.
        _stripe.CreateDelay = TimeSpan.FromMilliseconds(400);
        using var first = SupplierClient(userId);
        using var second = SupplierClient(userId);

        var responses = await Task.WhenAll(
            first.PostAsync($"{Base}/onboarding-link", null),
            second.PostAsync($"{Base}/onboarding-link", null));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var urls = new List<string?>();
        foreach (var response in responses)
            urls.Add((await ReadAsync(response)).GetProperty("url").GetString());

        // The advisory lock OrgConnectAccount serialized them: one account, and both links are for it.
        Assert.Equal(1, _stripe.CreateAccountCallCount);
        Assert.Equal(new[] { $"connect-account:{orgId:N}" }, _stripe.IdempotencyKeys);
        Assert.Single(urls.Distinct());
        Assert.EndsWith((await GetOrgAsync(orgId)).StripeConnectedAccountId!, urls[0]);
    }

    [PostgresFact]
    public async Task CreateAccount_ParallelWithOnboardingLink_CreatesOneAccount()
    {
        Assert.True(_factory.UsesPostgreSql);
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(_factory);
        _stripe.CreateDelay = TimeSpan.FromMilliseconds(400);
        using var first = SupplierClient(userId);
        using var second = SupplierClient(userId);

        var responses = await Task.WhenAll(
            first.PostAsync($"{Base}/account", null),
            second.PostAsync($"{Base}/onboarding-link", null));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, _stripe.CreateAccountCallCount);
        Assert.NotNull((await GetOrgAsync(orgId)).StripeConnectedAccountId);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient SupplierClient(string userId) => _factory.CreateAuthenticatedClient(userId, roles: "Supplier");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private async Task LinkAccountAsync(Guid orgId, string accountId, bool chargesEnabled, bool payoutsEnabled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        org.StripeConnectedAccountId = accountId;
        org.ConnectChargesEnabled = chargesEnabled;
        org.ConnectPayoutsEnabled = payoutsEnabled;
        org.ConnectDetailsSubmitted = chargesEnabled || payoutsEnabled;
        await db.SaveChangesAsync();
    }

    private async Task UpdateProfileAsync(Guid orgId, Action<SupplierProfile> change)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == orgId);
        change(profile);
        await db.SaveChangesAsync();
    }

    private async Task<SupplierProfile> GetProfileAsync(Guid orgId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierProfiles.AsNoTracking().SingleAsync(p => p.OrgId == orgId);
    }

    private async Task<OrgEntity> GetOrgAsync(Guid orgId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
    }
}
