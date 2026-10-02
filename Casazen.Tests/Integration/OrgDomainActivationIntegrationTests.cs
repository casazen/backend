using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-17 (A3-25): the owner's view of a custom domain end to end. DNS and the Vercel API are doubles (no network): the
/// domain is "Verified" only when the ownership TXT, the DNS and the Vercel project all say so, and the page gets the
/// honest state, the reason in the language of the request and the records to create.
/// </summary>
public class OrgDomainActivationIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public OrgDomainActivationIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetDomain_PendingCustomDomain_ShowsRecordsStatusAndNoLinkToASiteThatIsNotServed()
    {
        await using var arranged = await ArrangeAsync(new Doubles());
        var (client, org) = (arranged.Client, arranged.Org);
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SetCustomDomainAsync(client, org.Id, domain);

        var config = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain");

        Assert.Equal("Pending", config.GetProperty("domainVerificationStatus").GetString());
        var records = config.GetProperty("dnsInstructions");
        Assert.Equal(domain, records.GetProperty("cnameHost").GetString());
        Assert.False(string.IsNullOrWhiteSpace(records.GetProperty("cnameTarget").GetString()));
        Assert.StartsWith("_casazen-challenge.", records.GetProperty("txtHost").GetString(), StringComparison.Ordinal);
        Assert.NotEmpty(records.GetProperty("aRecordValues").EnumerateArray());
        var status = config.GetProperty("status");
        Assert.True(status.GetProperty("autoCheckActive").GetBoolean());
        Assert.False(status.GetProperty("activationAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("checkedAt").ValueKind);
        // A domain that is not served is not "your site" yet.
        Assert.Equal(JsonValueKind.Null, config.GetProperty("publicUrls").GetProperty("customDomainUrl").ValueKind);
    }

    [Fact]
    public async Task Verify_EverythingInPlace_IsVerifiedAndTheHostIsServed()
    {
        var doubles = new Doubles { VercelConfigured = true };
        await using var arranged = await ArrangeAsync(doubles);
        var (client, org) = (arranged.Client, arranged.Org);
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SetCustomDomainAsync(client, org.Id, domain);
        var records = (await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain")).GetProperty("dnsInstructions");
        doubles.Txt[$"_casazen-challenge.{domain}"] = [records.GetProperty("txtValue").GetString()!];
        doubles.Cname[domain] = [records.GetProperty("cnameTarget").GetString()!];
        doubles.VercelDomains[domain] = true;

        var verify = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain/verify", new { });

        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var result = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Verified", result.GetProperty("domainVerificationStatus").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("detail").ValueKind);

        var config = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain");
        Assert.Equal($"https://{domain}", config.GetProperty("publicUrls").GetProperty("customDomainUrl").GetString());
        Assert.True(config.GetProperty("status").GetProperty("activationAvailable").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, config.GetProperty("status").GetProperty("verifiedAt").ValueKind);

        using var publicClient = arranged.App.CreateClient();
        var resolve = await publicClient.GetAsync($"/api/public/resolve-host?host={domain}");
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
    }

    [Fact]
    public async Task Verify_OnlyTheTxtIsThere_IsPendingWithTheReasonInItalianByDefaultAndEnglishOnRequest()
    {
        var doubles = new Doubles { VercelConfigured = true };
        await using var arranged = await ArrangeAsync(doubles);
        var (client, org) = (arranged.Client, arranged.Org);
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SetCustomDomainAsync(client, org.Id, domain);
        var txtValue = (await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain"))
            .GetProperty("dnsInstructions").GetProperty("txtValue").GetString()!;
        doubles.Txt[$"_casazen-challenge.{domain}"] = [txtValue];

        var italian = await (await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain/verify", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        using var englishRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/orgs/{org.Id}/domain/verify")
        {
            Content = JsonContent.Create(new { }),
        };
        englishRequest.Headers.Add("Accept-Language", "en");
        var english = await (await client.SendAsync(englishRequest)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Pending", italian.GetProperty("domainVerificationStatus").GetString());
        Assert.Equal("dns_not_pointing", italian.GetProperty("detail").GetString());
        Assert.Contains("CNAME", italian.GetProperty("message").GetString());
        Assert.Contains("punta", italian.GetProperty("message").GetString());
        Assert.Contains("does not point", english.GetProperty("message").GetString());

        var config = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain");
        Assert.Equal("dns_not_pointing", config.GetProperty("status").GetProperty("detail").GetString());
        Assert.NotEqual(JsonValueKind.Null, config.GetProperty("status").GetProperty("checkedAt").ValueKind);
    }

    [Fact]
    public async Task Verify_VercelNotConfigured_IsPendingNeverVerifiedAndTheStatusSaysActivationIsUnavailable()
    {
        var doubles = new Doubles { VercelConfigured = false };
        await using var arranged = await ArrangeAsync(doubles);
        var (client, org) = (arranged.Client, arranged.Org);
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SetCustomDomainAsync(client, org.Id, domain);
        var records = (await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain")).GetProperty("dnsInstructions");
        doubles.Txt[$"_casazen-challenge.{domain}"] = [records.GetProperty("txtValue").GetString()!];
        doubles.Cname[domain] = [records.GetProperty("cnameTarget").GetString()!];

        var result = await (await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain/verify", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Pending", result.GetProperty("domainVerificationStatus").GetString());
        Assert.Equal("vercel_not_configured", result.GetProperty("detail").GetString());
        var config = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain");
        Assert.False(config.GetProperty("status").GetProperty("activationAvailable").GetBoolean());
        Assert.Equal(0, doubles.VercelCalls);

        using var publicClient = arranged.App.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync($"/api/public/resolve-host?host={domain}")).StatusCode);
    }

    [Fact]
    public async Task SetDomain_SameDomainSavedAgainWhilePending_KeepsTheTokenTheHostAlreadyPublished()
    {
        await using var arranged = await ArrangeAsync(new Doubles());
        var (client, org) = (arranged.Client, arranged.Org);
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var first = await SetCustomDomainAsync(client, org.Id, domain);

        var second = await SetCustomDomainAsync(client, org.Id, domain);

        Assert.Equal(
            first.GetProperty("dnsInstructions").GetProperty("txtValue").GetString(),
            second.GetProperty("dnsInstructions").GetProperty("txtValue").GetString());
    }

    [Fact]
    public async Task SetDomain_AnotherDomain_GetsANewTokenAndForgetsTheStateOfTheOldOne()
    {
        await using var arranged = await ArrangeAsync(new Doubles());
        var (client, org) = (arranged.Client, arranged.Org);
        var first = await SetCustomDomainAsync(client, org.Id, $"www.{Guid.NewGuid():N}.example.test");
        await SetCheckStateAsync(org.Id, o =>
        {
            o.DomainStatusDetail = DomainIssues.DnsNotPointing;
            o.DomainCheckedAt = DateTime.UtcNow;
        });

        var second = await SetCustomDomainAsync(client, org.Id, $"www.{Guid.NewGuid():N}.example.test");

        Assert.NotEqual(
            first.GetProperty("dnsInstructions").GetProperty("txtValue").GetString(),
            second.GetProperty("dnsInstructions").GetProperty("txtValue").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("status").GetProperty("detail").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("status").GetProperty("checkedAt").ValueKind);
    }

    [Fact]
    public async Task SetDomain_DroppingADomainThatWasOnVercel_QueuesItsRemovalFromTheProject()
    {
        await using var arranged = await ArrangeAsync(new Doubles { VercelConfigured = true });
        var (client, org) = (arranged.Client, arranged.Org);
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SetCustomDomainAsync(client, org.Id, domain);
        await SetCheckStateAsync(org.Id, o => o.DomainVercelAddedAt = DateTime.UtcNow);

        var back = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new { hostMode = PublicHostMode.CasazenPath });

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        using var scope = arranged.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(await db.PendingDomainRemovals.ToListAsync(), r => r.Domain == domain);
        var saved = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Null(saved.DomainVercelAddedAt);
        Assert.Null(saved.CustomDomain);
    }

    [Fact]
    public async Task SetDomain_DroppingADomainThatWasNeverOnVercel_QueuesNothing()
    {
        await using var arranged = await ArrangeAsync(new Doubles { VercelConfigured = true });
        var (client, org) = (arranged.Client, arranged.Org);
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SetCustomDomainAsync(client, org.Id, domain);

        await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new { hostMode = PublicHostMode.CasazenPath });

        using var scope = arranged.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.DoesNotContain(await db.PendingDomainRemovals.ToListAsync(), r => r.Domain == domain);
    }

    [Theory]
    [InlineData("casazen-app.vercel.app")]
    [InlineData("shop.casazen-app.vercel.app")]
    [InlineData("anything.vercel.app")]
    [InlineData("https://CASAZEN-APP.vercel.app/")]
    public async Task SetDomain_TheWebAppsOwnDomainOrAVercelName_IsRejected(string domain)
    {
        // The custom domain is added to and removed from the Vercel project that serves the app: never the app's own name.
        await using var arranged = await ArrangeAsync(new Doubles { VercelConfigured = true });

        var response = await arranged.Client.PostAsJsonAsync($"/api/orgs/{arranged.Org.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = domain,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_domain_invalid", problem.GetProperty("code").GetString());
        Assert.Equal("Il dominio personalizzato non è valido.", problem.GetProperty("detail").GetString());
    }

    // ─── Arrange ───────────────────────────────────────────────────────────────────────────────────

    private async Task<Arrangement> ArrangeAsync(Doubles doubles)
    {
        var ownerId = $"auth0|activation-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(org.Id);
        var app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDnsTxtLookup>();
            services.RemoveAll<IDnsRecordLookup>();
            services.RemoveAll<IVercelDomainsClient>();
            services.AddSingleton<IDnsTxtLookup>(doubles);
            services.AddSingleton<IDnsRecordLookup>(doubles);
            services.AddSingleton<IVercelDomainsClient>(doubles);
        }));
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", ownerId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "PropertyOwner");
        return new Arrangement(app, client, org);
    }

    private sealed record Arrangement(WebApplicationFactory<Program> App, HttpClient Client, OrgEntity Org) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<JsonElement> SetCustomDomainAsync(HttpClient client, Guid orgId, string domain)
    {
        var response = await client.PostAsJsonAsync($"/api/orgs/{orgId}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = domain,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SetPlanAsync(Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        org!.PlanTier = PlanTier.Pro;
        org.SubscriptionId = $"sub_test_{orgId:N}";
        org.SubscriptionStatus = SubscriptionStatus.Active;
        await db.SaveChangesAsync();
    }

    private async Task SetCheckStateAsync(Guid orgId, Action<OrgEntity> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        change(org!);
        await db.SaveChangesAsync();
    }

    /// <summary>DNS and Vercel in one double: what the records say and which domains Vercel has, per test.</summary>
    private sealed class Doubles : IDnsTxtLookup, IDnsRecordLookup, IVercelDomainsClient
    {
        public Dictionary<string, string[]> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Cname { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Domains on the Vercel project and whether Vercel has verified them.</summary>
        public Dictionary<string, bool> VercelDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool VercelConfigured { get; set; }

        public int VercelCalls { get; private set; }

        bool IVercelDomainsClient.IsConfigured => VercelConfigured;

        public Task<IReadOnlyList<string>> LookupTxtAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Txt.TryGetValue(host, out var values) ? values : []);

        public Task<IReadOnlyList<string>> LookupCnameAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Cname.TryGetValue(host, out var values) ? values : []);

        public Task<IReadOnlyList<string>> LookupAddressesAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<VercelCallResult<VercelDomain>> GetDomainAsync(string domain, CancellationToken cancellationToken = default)
        {
            VercelCalls++;
            return Task.FromResult(VercelDomains.TryGetValue(domain, out var verified)
                ? new VercelCallResult<VercelDomain>(VercelCallStatus.Ok, new VercelDomain(domain, verified, []))
                : new VercelCallResult<VercelDomain>(VercelCallStatus.NotFound));
        }

        public Task<VercelCallResult<VercelDomain>> AddDomainAsync(string domain, CancellationToken cancellationToken = default)
        {
            VercelCalls++;
            VercelDomains[domain] = true;
            return Task.FromResult(new VercelCallResult<VercelDomain>(VercelCallStatus.Ok, new VercelDomain(domain, true, [])));
        }

        public Task<VercelCallResult<VercelDomain>> VerifyDomainAsync(string domain, CancellationToken cancellationToken = default)
        {
            VercelCalls++;
            return Task.FromResult(new VercelCallResult<VercelDomain>(VercelCallStatus.Ok, new VercelDomain(domain, VercelDomains.GetValueOrDefault(domain), [])));
        }

        public Task<VercelCallResult<bool>> RemoveDomainAsync(string domain, CancellationToken cancellationToken = default)
        {
            VercelCalls++;
            return Task.FromResult(new VercelCallResult<bool>(VercelDomains.Remove(domain) ? VercelCallStatus.Ok : VercelCallStatus.NotFound, true));
        }
    }
}
