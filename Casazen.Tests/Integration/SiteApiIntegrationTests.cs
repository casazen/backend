using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// S023 (PLG-BE-SITEAPI, site integration plan §10.3-§10.4, NF-202, NF-204, AC 1 to 6) over the real pipeline, with the credential of a connected site: the account and
/// the domain of the org of the credential only (the isolation between two orgs, a body that names another org), the same answers as the console, the surfaces behind
/// <c>WhiteLabelHost</c>, a revoked credential and a sign-in of a person 401, every endpoint 404 while <c>SiteEmbed</c> is off, the commercial module of the console's
/// domain endpoints (and never on the disconnection), and the limit of the check of the domain per credential.
/// </summary>
public class SiteApiIntegrationTests(
    CasazenWebApplicationFactory flagsOff,
    SiteIntegrationFlagsFactory flagsOn,
    SiteConnectionIntegrationTests.ConnectFactory embedOnly)
    : IClassFixture<CasazenWebApplicationFactory>, IClassFixture<SiteIntegrationFlagsFactory>, IClassFixture<SiteConnectionIntegrationTests.ConnectFactory>
{
    private static readonly object AllOn = new { guestAreaOnOwnDomain = true, ownerPortalOnOwnDomain = true, useOwnDomainForLinks = true };

    [Fact]
    public async Task Account_TheShapeOfThePlan_CasazenWithoutADomain_TheOwnDomainWhenItIsServed_AndTheConsolesDomainObject()
    {
        var (org, adminId) = await SeedAgencyAsync(flagsOn);
        using var plugin = PluginClient(flagsOn, await ConnectAsync(flagsOn, org.Id));

        var casazen = await plugin.GetAsync("/api/site/v1/account");

        Assert.Equal(HttpStatusCode.OK, casazen.StatusCode);
        Assert.True(casazen.Headers.CacheControl?.NoStore);
        var account = await casazen.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(org.Id, account.GetProperty("org").GetProperty("id").GetGuid());
        Assert.Equal(org.Slug, account.GetProperty("org").GetProperty("slug").GetString());
        Assert.False(string.IsNullOrWhiteSpace(account.GetProperty("org").GetProperty("displayName").GetString()));
        Assert.NotEqual(Guid.Empty, account.GetProperty("connectionId").GetGuid());
        Assert.Equal("casazen", account.GetProperty("publicAddressKind").GetString());
        Assert.Equal(CasazenAddress(flagsOn), account.GetProperty("publicAddress").GetString());
        Assert.True(account.GetProperty("embedEnabled").GetBoolean());
        Assert.Equal(org.Id, account.GetProperty("domain").GetProperty("orgId").GetGuid());

        // Served on its own verified domain: its origin, the kind customDomain, and the embedEnabled that resolve-host gives for that host.
        var domain = $"served-{Guid.NewGuid():N}.example.it";
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(flagsOn, org.Id, domain);
        var served = await plugin.GetFromJsonAsync<JsonElement>("/api/site/v1/account");
        Assert.Equal("customDomain", served.GetProperty("publicAddressKind").GetString());
        Assert.Equal($"https://{domain}", served.GetProperty("publicAddress").GetString());
        using var publicClient = flagsOn.CreateClient();
        var resolved = await publicClient.GetFromJsonAsync<JsonElement>($"/api/public/resolve-host?host={domain}");
        Assert.Equal(resolved.GetProperty("embedEnabled").GetBoolean(), served.GetProperty("embedEnabled").GetBoolean());

        // domain: the object GET api/orgs/{orgId}/domain gives the console, member by member.
        using var admin = flagsOn.CreateAuthenticatedClient(adminId, "PropertyOwner");
        var console = await admin.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain");
        Assert.Equal(console.GetRawText(), served.GetProperty("domain").GetRawText());
    }

    [Fact]
    public async Task SetDomain_AValidDomain_IsStored_TheRefusalsAreTheConsolesOwn_NullGoesBackToCasazen()
    {
        var (org, _) = await SeedAgencyAsync(flagsOn);
        var (other, _) = await SeedAgencyAsync(flagsOn);
        var takenDomain = $"taken-{Guid.NewGuid():N}.example.it";
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(flagsOn, other.Id, takenDomain);
        using var plugin = PluginClient(flagsOn, await ConnectAsync(flagsOn, org.Id));
        var domain = $"prenota-{Guid.NewGuid():N}.example.it";

        var saved = await plugin.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = domain });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var account = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var config = account.GetProperty("domain");
        Assert.Equal(domain, config.GetProperty("customDomain").GetString());
        Assert.Equal("Pending", config.GetProperty("domainVerificationStatus").GetString());
        Assert.Equal(domain, config.GetProperty("dnsInstructions").GetProperty("cnameHost").GetString());
        Assert.StartsWith("_casazen-challenge.", config.GetProperty("dnsInstructions").GetProperty("txtHost").GetString(), StringComparison.Ordinal);
        Assert.Equal("casazen", account.GetProperty("publicAddressKind").GetString());
        var stored = await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id);
        Assert.Equal((PublicHostMode.CustomDomain, domain), (stored.PublicHostMode, stored.CustomDomain));

        await AssertProblemAsync(await plugin.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = "not a domain" }), HttpStatusCode.BadRequest, "org_domain_invalid");
        await AssertProblemAsync(await plugin.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = takenDomain }), HttpStatusCode.Conflict, "org_domain_in_use");
        // `{}` is a client that dropped its empty values, not «go back to CasaZen».
        await AssertProblemAsync(await plugin.PutAsJsonAsync("/api/site/v1/domain", new { }), HttpStatusCode.BadRequest, "org_domain_invalid");
        Assert.Equal(domain, (await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id)).CustomDomain);

        var back = await plugin.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = (string?)null });

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        var backAccount = await back.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, backAccount.GetProperty("domain").GetProperty("customDomain").ValueKind);
        Assert.Equal("CasazenPath", backAccount.GetProperty("domain").GetProperty("publicHostMode").GetString());
        Assert.Equal("casazen", backAccount.GetProperty("publicAddressKind").GetString());
        Assert.Null((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id)).CustomDomain);
    }

    [Fact]
    public async Task SetDomain_OnAPlanWithoutTheCustomDomain_403PlanRequired_WithTheRequiredPlan()
    {
        var (org, _) = await SeedAgencyAsync(flagsOn, PlanTier.Starter);
        using var plugin = PluginClient(flagsOn, await ConnectAsync(flagsOn, org.Id));

        var refused = await plugin.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = $"prenota-{Guid.NewGuid():N}.example.it" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan_required", body.GetProperty("code").GetString());
        Assert.Equal("Pro", body.GetProperty("requiredPlan").GetString());
        Assert.Null((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id)).CustomDomain);
    }

    [Fact]
    public async Task Verify_WithoutADomain_400_AndTwentyInTenMinutesPerCredential()
    {
        var (org, _) = await SeedAgencyAsync(flagsOn);
        var (other, _) = await SeedAgencyAsync(flagsOn);
        using var plugin = PluginClient(flagsOn, await ConnectAsync(flagsOn, org.Id));
        using var otherPlugin = PluginClient(flagsOn, await ConnectAsync(flagsOn, other.Id));

        // Without a domain: 400 and no check. Every call counts against the budget of its credential.
        for (var i = 0; i < 20; i++)
            await AssertProblemAsync(await plugin.PostAsync("/api/site/v1/domain/verify", content: null), HttpStatusCode.BadRequest, "org_domain_not_configured");

        var limited = await plugin.PostAsync("/api/site/v1/domain/verify", content: null);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.True(limited.Headers.CacheControl?.NoStore);
        // Another credential has its own budget, and the other endpoints of the limited one are not touched by it.
        await AssertProblemAsync(await otherPlugin.PostAsync("/api/site/v1/domain/verify", content: null), HttpStatusCode.BadRequest, "org_domain_not_configured");
        Assert.Equal(HttpStatusCode.OK, (await plugin.GetAsync("/api/site/v1/account")).StatusCode);
    }

    [Fact]
    public async Task Verify_ADomainTheDnsDoesNotPointAt_IsPending_WithTheCheckAndTheAccountAfterIt()
    {
        var (org, _) = await SeedAgencyAsync(flagsOn);
        var domain = $"prenota-{Guid.NewGuid():N}.example.it";
        await OrgDomainSurfacesIntegrationTests.UpdateOrgAsync(flagsOn, org.Id, o =>
        {
            o.PublicHostMode = PublicHostMode.CustomDomain;
            o.CustomDomain = domain;
            o.DomainVerificationStatus = DomainVerificationStatus.Pending;
            o.DomainVerificationToken = "verify-token";
        });
        var credential = await ConnectAsync(flagsOn, org.Id);
        await using var app = WithPendingVerification(flagsOn);
        using var plugin = PluginClient(app, credential);

        var response = await plugin.PostAsync("/api/site/v1/domain/verify", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var verify = body.GetProperty("verify");
        Assert.Equal("Pending", verify.GetProperty("domainVerificationStatus").GetString());
        Assert.Equal(domain, verify.GetProperty("customDomain").GetString());
        Assert.Equal(DomainIssues.DnsNotPointing, verify.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(verify.GetProperty("message").GetString()));
        var account = body.GetProperty("account");
        Assert.Equal(org.Id, account.GetProperty("org").GetProperty("id").GetGuid());
        Assert.Equal("casazen", account.GetProperty("publicAddressKind").GetString());
        Assert.Equal(domain, account.GetProperty("domain").GetProperty("customDomain").GetString());
    }

    [Fact]
    public async Task Surfaces_SaveTheThreeChoices_TheAnswerCarriesThem_TheRefusalsAreTheConsolesOwn()
    {
        var (org, _) = await SeedAgencyAsync(flagsOn);
        var (noDomain, _) = await SeedAgencyAsync(flagsOn);
        var (starter, _) = await SeedAgencyAsync(flagsOn, PlanTier.Starter);
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(flagsOn, org.Id, $"surf-{Guid.NewGuid():N}.example.it");
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(flagsOn, starter.Id, $"lapsed-{Guid.NewGuid():N}.example.it");
        using var plugin = PluginClient(flagsOn, await ConnectAsync(flagsOn, org.Id));

        var saved = await plugin.PutAsJsonAsync("/api/site/v1/domain/surfaces", new { guestAreaOnOwnDomain = true, ownerPortalOnOwnDomain = false, useOwnDomainForLinks = true });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var surfaces = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("domain").GetProperty("surfaces");
        Assert.True(surfaces.GetProperty("guestAreaOnOwnDomain").GetBoolean());
        Assert.False(surfaces.GetProperty("ownerPortalOnOwnDomain").GetBoolean());
        Assert.True(surfaces.GetProperty("useOwnDomainForLinks").GetBoolean());
        var stored = await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id);
        Assert.Equal((true, false, true), (stored.GuestAreaOnOwnDomain, stored.OwnerPortalOnOwnDomain, stored.UseOwnDomainForLinks));

        // The three choices are required, as in the console.
        Assert.Equal(HttpStatusCode.BadRequest, (await plugin.PutAsJsonAsync("/api/site/v1/domain/surfaces", new { guestAreaOnOwnDomain = true })).StatusCode);
        using var withoutDomain = PluginClient(flagsOn, await ConnectAsync(flagsOn, noDomain.Id));
        await AssertProblemAsync(await withoutDomain.PutAsJsonAsync("/api/site/v1/domain/surfaces", AllOn), HttpStatusCode.BadRequest, "org_domain_not_configured");
        using var lapsed = PluginClient(flagsOn, await ConnectAsync(flagsOn, starter.Id));
        var plan = await lapsed.PutAsJsonAsync("/api/site/v1/domain/surfaces", AllOn);
        Assert.Equal(HttpStatusCode.Forbidden, plan.StatusCode);
        Assert.Equal("plan_required", (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Surfaces_WhileWhiteLabelHostIsOff_404_TheOtherEndpointsStillAnswer()
    {
        var (org, _) = await SeedAgencyAsync(embedOnly);
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(embedOnly, org.Id, $"surf-{Guid.NewGuid():N}.example.it");
        using var plugin = PluginClient(embedOnly, await ConnectAsync(embedOnly, org.Id));

        var surfaces = await plugin.PutAsJsonAsync("/api/site/v1/domain/surfaces", AllOn);

        Assert.Equal(HttpStatusCode.NotFound, surfaces.StatusCode);
        var account = await plugin.GetAsync("/api/site/v1/account");
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        // Without WhiteLabelHost the domain has no surfaces block, as in the console.
        Assert.Equal(JsonValueKind.Null, (await account.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("domain").GetProperty("surfaces").ValueKind);
        Assert.False((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(embedOnly, org.Id)).GuestAreaOnOwnDomain);
    }

    [Fact]
    public async Task Isolation_TheCredentialOfOrgA_NeverReadsOrChangesOrgB_ABodyThatNamesBIsIgnored()
    {
        var (orgA, _) = await SeedAgencyAsync(flagsOn);
        var (orgB, adminB) = await SeedAgencyAsync(flagsOn);
        var domainB = $"orgb-{Guid.NewGuid():N}.example.it";
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(flagsOn, orgB.Id, domainB);
        var credentialA = await ConnectAsync(flagsOn, orgA.Id);
        using var pluginB = PluginClient(flagsOn, await ConnectAsync(flagsOn, orgB.Id));
        await using var app = WithPendingVerification(flagsOn);
        using var pluginA = PluginClient(app, credentialA);
        var beforeB = await StateAsync(flagsOn, orgB.Id);

        var account = await pluginA.GetFromJsonAsync<JsonElement>("/api/site/v1/account");
        Assert.Equal(orgA.Id, account.GetProperty("org").GetProperty("id").GetGuid());
        Assert.DoesNotContain(domainB, account.GetRawText(), StringComparison.Ordinal);

        // The org is the credential's: the address, the query and the body may name B, nothing of B moves.
        var domainA = $"orga-{Guid.NewGuid():N}.example.it";
        var changed = await pluginA.PutAsJsonAsync(
            $"/api/site/v1/domain?orgId={orgB.Id}",
            new { customDomain = domainA, orgId = orgB.Id, org = new { id = orgB.Id } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(orgA.Id, (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("domain").GetProperty("orgId").GetGuid());
        Assert.Equal(domainA, (await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, orgA.Id)).CustomDomain);

        var surfaces = await pluginA.PutAsJsonAsync(
            $"/api/site/v1/domain/surfaces?orgId={orgB.Id}",
            new { guestAreaOnOwnDomain = true, ownerPortalOnOwnDomain = true, useOwnDomainForLinks = true, orgId = orgB.Id });
        Assert.Equal(HttpStatusCode.OK, surfaces.StatusCode);
        Assert.True((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, orgA.Id)).GuestAreaOnOwnDomain);

        var verify = await pluginA.PostAsync($"/api/site/v1/domain/verify?orgId={orgB.Id}", content: null);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.Equal(domainA, (await verify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verify").GetProperty("customDomain").GetString());

        // The domain of B is B's: A cannot take it.
        await AssertProblemAsync(await pluginA.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = domainB }), HttpStatusCode.Conflict, "org_domain_in_use");

        Assert.Equal(beforeB, await StateAsync(flagsOn, orgB.Id));
        // B's own credential still reads B, and so does its administrator in the console.
        var accountB = await pluginB.GetFromJsonAsync<JsonElement>("/api/site/v1/account");
        Assert.Equal(orgB.Id, accountB.GetProperty("org").GetProperty("id").GetGuid());
        Assert.Equal(domainB, accountB.GetProperty("domain").GetProperty("customDomain").GetString());
        using var adminOfB = flagsOn.CreateAuthenticatedClient(adminB, "PropertyOwner");
        Assert.Equal(domainB, (await adminOfB.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgB.Id}/domain")).GetProperty("customDomain").GetString());
    }

    [Fact]
    public async Task Credential_APersonAndARevokedOne_401OnEveryEndpoint_ACredentialIsNoSignInOnTheConsole()
    {
        var (org, adminId) = await SeedAgencyAsync(flagsOn);
        using var plugin = PluginClient(flagsOn, await ConnectAsync(flagsOn, org.Id));
        using var person = flagsOn.CreateAuthenticatedClient(adminId, "PropertyOwner");
        using var anonymous = flagsOn.CreateClient();

        // A sign-in of a person is no credential of a site, and nobody without one gets in.
        await AssertUnauthorizedOnEveryEndpointAsync(person);
        await AssertUnauthorizedOnEveryEndpointAsync(anonymous);
        Assert.Equal(HttpStatusCode.OK, (await plugin.GetAsync("/api/site/v1/account")).StatusCode);
        // And a credential of a site is no sign-in of a person.
        Assert.Equal(HttpStatusCode.Unauthorized, (await plugin.GetAsync($"/api/orgs/{org.Id}/domain")).StatusCode);

        // Revoked (here by the plugin itself): the same credential is 401 everywhere, at once.
        Assert.Equal(HttpStatusCode.NoContent, (await plugin.DeleteAsync("/api/site/v1/connection")).StatusCode);
        await AssertUnauthorizedOnEveryEndpointAsync(plugin);
    }

    [Fact]
    public async Task FlagOff_EveryEndpointOfTheDomain_404BeforeAnything_AndNothingIsChanged()
    {
        var (org, _) = await SeedAgencyAsync(flagsOff);
        using var plugin = PluginClient(flagsOff, SiteConnectionTokens.NewCredential());

        HttpResponseMessage[] answers =
        [
            await plugin.GetAsync("/api/site/v1/account"),
            await plugin.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = $"prenota-{Guid.NewGuid():N}.example.it" }),
            await plugin.PostAsync("/api/site/v1/domain/verify", content: null),
            await plugin.PutAsJsonAsync("/api/site/v1/domain/surfaces", AllOn),
        ];

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode));
        Assert.Null((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOff, org.Id)).CustomDomain);
    }

    [Fact]
    public async Task CommercialModule_TheDomainOperationsNeedIt_TheDisconnectionNeverDoes()
    {
        var (org, _) = await SeedAgencyAsync(flagsOn);
        await OrgDomainSurfacesIntegrationTests.UpdateOrgAsync(flagsOn, org.Id, o =>
        {
            o.CommercialSignupJson = "{}";
            o.CommercialPaymentConfirmed = true;
            o.SubscriptionStatus = SubscriptionStatus.Active;
            o.CommercialPaidAddonsJson = "[]";
        });
        using var plugin = PluginClient(flagsOn, await ConnectAsync(flagsOn, org.Id));

        // An agency of a commercial sign-up without the add-on direct-booking gets what the console's OrgDomain endpoints give.
        HttpResponseMessage[] refused =
        [
            await plugin.GetAsync("/api/site/v1/account"),
            await plugin.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = $"prenota-{Guid.NewGuid():N}.example.it" }),
            await plugin.PostAsync("/api/site/v1/domain/verify", content: null),
            await plugin.PutAsJsonAsync("/api/site/v1/domain/surfaces", AllOn),
            // It answers before the body is read: the same 403 for a body that is no JSON.
            await plugin.PutAsync("/api/site/v1/domain", new StringContent("not json", Encoding.UTF8, "application/json")),
        ];
        foreach (var answer in refused)
        {
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            var body = await answer.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("commercial_module_required", body.GetProperty("code").GetString());
            Assert.Equal("direct-booking", body.GetProperty("module").GetString());
        }

        Assert.Null((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id)).CustomDomain);

        // With the module every operation passes.
        await OrgDomainSurfacesIntegrationTests.UpdateOrgAsync(flagsOn, org.Id, o => o.CommercialPaidAddonsJson = "[\"direct-booking\"]");
        Assert.Equal(HttpStatusCode.OK, (await plugin.GetAsync("/api/site/v1/account")).StatusCode);

        // And a lapsed module never leaves a credential that cannot be revoked.
        await OrgDomainSurfacesIntegrationTests.UpdateOrgAsync(flagsOn, org.Id, o => o.CommercialPaidAddonsJson = "[]");
        Assert.Equal(HttpStatusCode.Forbidden, (await plugin.GetAsync("/api/site/v1/account")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await plugin.DeleteAsync("/api/site/v1/connection")).StatusCode);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>An agency with a billing administrator and a plan (Pro by default: the custom domain is a Pro feature).</summary>
    private static async Task<(OrgEntity Org, string AdminId)> SeedAgencyAsync(CasazenWebApplicationFactory factory, PlanTier tier = PlanTier.Pro)
    {
        var adminId = $"auth0|site-api-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(adminId);
        await OrgDomainSurfacesIntegrationTests.SetPlanAsync(factory, org.Id, tier);
        return (org, adminId);
    }

    /// <summary>
    /// A connected site of <paramref name="orgId"/>: a row of the connections with the hash of a new credential, as the exchange of the code writes it (the whole flow is
    /// proved by <c>SiteConnectionIntegrationTests</c>). The credential is returned and never stored.
    /// </summary>
    private static async Task<string> ConnectAsync(CasazenWebApplicationFactory factory, Guid orgId)
    {
        var credential = SiteConnectionTokens.NewCredential();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SiteConnections.Add(new SiteConnection
        {
            OrgId = orgId,
            SiteUrl = $"https://www.{Guid.NewGuid():N}.example",
            TokenHash = SiteConnectionTokens.Hash(credential),
            CreatedByUserId = "auth0|site-api-test",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return credential;
    }

    private static HttpClient PluginClient(WebApplicationFactory<Program> factory, string credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    /// <summary>The same host (the same database) with a check of the domain that finds nothing: no DNS, no Vercel.</summary>
    private static WebApplicationFactory<Program> WithPendingVerification(CasazenWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDomainVerificationService>();
            services.AddSingleton<IDomainVerificationService, PendingVerification>();
        }));

    private static string CasazenAddress(CasazenWebApplicationFactory factory)
    {
        var configured = factory.Services.GetRequiredService<IOptions<PublicSiteOptions>>().Value.PublicSiteBaseUrl;
        Assert.True(PublicSiteOptions.TryGetOrigin(configured, out var origin));
        return origin;
    }

    /// <summary>What an org stores of its domain and its surfaces, as one text: it must not move when another org's credential acts.</summary>
    private static async Task<string> StateAsync(CasazenWebApplicationFactory factory, Guid orgId)
    {
        var org = await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(factory, orgId);
        return string.Join(
            '|',
            org.PublicHostMode,
            org.Subdomain,
            org.CustomDomain,
            org.DomainVerificationStatus,
            org.DomainVerificationToken,
            org.GuestAreaOnOwnDomain,
            org.OwnerPortalOnOwnDomain,
            org.UseOwnDomainForLinks,
            org.OwnerLoginHostState,
            org.UpdatedAt.ToString("O"));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static async Task AssertUnauthorizedOnEveryEndpointAsync(HttpClient client)
    {
        HttpResponseMessage[] answers =
        [
            await client.GetAsync("/api/site/v1/account"),
            await client.PutAsJsonAsync("/api/site/v1/domain", new { customDomain = "prenota.example.it" }),
            await client.PostAsync("/api/site/v1/domain/verify", content: null),
            await client.PutAsJsonAsync("/api/site/v1/domain/surfaces", AllOn),
            await client.DeleteAsync("/api/site/v1/connection"),
        ];

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode));
    }

    /// <summary>The check of the domain with nothing to find: the records are not there yet.</summary>
    private sealed class PendingVerification : IDomainVerificationService
    {
        public Task<DomainVerificationResult> VerifyAsync(OrgEntity org, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DomainVerificationResult(DomainVerificationStatus.Pending, org.CustomDomain!, DateTime.UtcNow, DomainIssues.DnsNotPointing));
    }
}
