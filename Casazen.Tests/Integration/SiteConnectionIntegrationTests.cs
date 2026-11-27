using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Casazen.Core.SiteConnections;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// S022 (PLG-BE-CONNECT, site integration plan §10.3-§10.4, NF-201, NF-203, NF-204) over the real pipeline: the whole flow from the console's confirmation to
/// the plugin disconnecting itself, the one 400 of every refused exchange, the schemes kept apart (a sign-in of a person on the plugin's API and a credential
/// on the console are 401), the isolation between two orgs, the newest connection of a site revoking the older, the refusals of the confirmation, the limit
/// of the exchange per address, and every new endpoint answering 404 while <c>SiteEmbed</c> is off.
/// </summary>
public class SiteConnectionIntegrationTests(CasazenWebApplicationFactory flagsOff, SiteConnectionIntegrationTests.ConnectFactory flagsOn)
    : IClassFixture<CasazenWebApplicationFactory>, IClassFixture<SiteConnectionIntegrationTests.ConnectFactory>
{
    private static int _nextPeer;

    [Fact]
    public async Task Connect_TheWholeFlow_TheCredentialIsShownOnce_ThenThePluginDisconnectsItself()
    {
        var (orgId, adminId) = await SeedAgencyAsync(flagsOn);
        var site = NewSite();
        var (verifier, challenge) = Pkce();
        using var admin = flagsOn.CreateAuthenticatedClient(adminId, "PropertyOwner");

        var authorized = await admin.PostAsJsonAsync($"/api/orgs/{orgId}/site-connections/authorize", AuthorizeBody(site, challenge));

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.True(authorized.Headers.CacheControl?.NoStore);
        var authorization = await authorized.Content.ReadFromJsonAsync<JsonElement>();
        var redirect = authorization.GetProperty("redirectUrl").GetString()!;
        Assert.StartsWith(site + "/wp-admin/options-general.php?page=casazen&casazen_code=", redirect, StringComparison.Ordinal);
        Assert.Equal(site, authorization.GetProperty("siteUrl").GetString());
        Assert.True(authorization.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow);
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)[SiteConnectionRules.CodeQueryParameter].ToString();

        var exchanged = await TokenAsync(code, verifier, site);

        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        Assert.True(exchanged.Headers.CacheControl?.NoStore);
        var grant = await exchanged.Content.ReadFromJsonAsync<JsonElement>();
        var credential = grant.GetProperty("accessToken").GetString()!;
        Assert.True(SiteConnectionTokens.IsCredential(credential));
        var connectionId = grant.GetProperty("connectionId").GetGuid();
        Assert.Equal(orgId, grant.GetProperty("org").GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(grant.GetProperty("org").GetProperty("slug").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(grant.GetProperty("org").GetProperty("displayName").GetString()));

        var listed = await admin.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/site-connections");
        var item = Assert.Single(listed.EnumerateArray());
        Assert.Equal(connectionId, item.GetProperty("id").GetGuid());
        Assert.Equal(site, item.GetProperty("siteUrl").GetString());
        Assert.DoesNotContain(credential, listed.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await DisconnectAsync(credential)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DisconnectAsync(credential)).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/site-connections")).EnumerateArray());

        // AC 5: the database holds the hashes only.
        using var scope = flagsOn.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var codes = await db.SiteConnectionCodes.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync();
        var connections = await db.SiteConnections.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync();
        Assert.Equal(SiteConnectionTokens.Hash(code), Assert.Single(codes).CodeHash);
        Assert.Equal(SiteConnectionTokens.Hash(credential), Assert.Single(connections).TokenHash);
        var stored = codes.SelectMany(c => new[] { c.CodeHash, c.CodeChallenge, c.SiteUrl, c.CreatedByUserId })
            .Concat(connections.SelectMany(c => new[] { c.TokenHash, c.SiteUrl, c.CreatedByUserId }))
            .ToList();
        foreach (var secret in new[] { code, credential, verifier })
            Assert.DoesNotContain(stored, value => value.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Token_UsedExpiredWrongVerifierOtherSiteOrUnknown_TheSame400InvalidGrant()
    {
        var (orgId, adminId) = await SeedAgencyAsync(flagsOn);
        var site = NewSite();
        var (verifier, challenge) = Pkce();
        using var admin = flagsOn.CreateAuthenticatedClient(adminId, "PropertyOwner");
        var used = await AuthorizeAsync(admin, orgId, site, challenge);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(used, verifier, site)).StatusCode);
        var expired = await AuthorizeAsync(admin, orgId, site, challenge);
        var wrongVerifier = await AuthorizeAsync(admin, orgId, site, challenge);
        var otherSite = await AuthorizeAsync(admin, orgId, site, challenge);
        await ExpireAsync(expired);

        HttpResponseMessage[] refusals =
        [
            await TokenAsync(used, verifier, site),
            await TokenAsync(expired, verifier, site),
            await TokenAsync(wrongVerifier, Pkce().Verifier, site),
            await TokenAsync(otherSite, verifier, NewSite()),
            await TokenAsync(SiteConnectionTokens.NewCode(), verifier, site),
        ];

        var bodies = new List<string>();
        foreach (var refusal in refusals)
        {
            Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
            var text = await refusal.Content.ReadAsStringAsync();
            Assert.DoesNotContain(verifier, text, StringComparison.Ordinal);
            var problem = JsonDocument.Parse(text).RootElement;
            Assert.Equal(SiteConnectionErrorCodes.InvalidGrant, problem.GetProperty("code").GetString());
            bodies.Add(JsonSerializer.Serialize(new
            {
                status = problem.GetProperty("status").GetInt32(),
                code = problem.GetProperty("code").GetString(),
                title = problem.GetProperty("title").GetString(),
                detail = problem.GetProperty("detail").GetString(),
            }));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        using var scope = flagsOn.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.SiteConnections.IgnoreQueryFilters().CountAsync(c => c.OrgId == orgId));
    }

    [Fact]
    public async Task Schemes_ASignInOfAPersonOnThePluginsApi_401_ACredentialOnTheConsole_401()
    {
        var (orgId, adminId) = await SeedAgencyAsync(flagsOn);
        var (credential, _) = await ConnectAsync(adminId, orgId, NewSite());

        using var admin = flagsOn.CreateAuthenticatedClient(adminId, "PropertyOwner");
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.DeleteAsync("/api/site/v1/connection")).StatusCode);

        using var plugin = flagsOn.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        Assert.Equal(HttpStatusCode.Unauthorized, (await plugin.GetAsync($"/api/orgs/{orgId}/site-connections")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await plugin.GetAsync($"/api/orgs/{orgId}/domain")).StatusCode);

        // Neither call touched the connection: the credential still opens the plugin's API.
        Assert.Equal(HttpStatusCode.NoContent, (await DisconnectAsync(credential)).StatusCode);
    }

    [Fact]
    public async Task Isolation_TheCredentialOfOrgA_NeverActsOnOrgB()
    {
        var (orgA, adminA) = await SeedAgencyAsync(flagsOn);
        var (orgB, adminB) = await SeedAgencyAsync(flagsOn);
        var site = NewSite();
        var (credentialA, connectionA) = await ConnectAsync(adminA, orgA, site);
        var (credentialB, connectionB) = await ConnectAsync(adminB, orgB, site);
        using var adminOfB = flagsOn.CreateAuthenticatedClient(adminB, "PropertyOwner");

        // B's administrator neither lists nor revokes A's connection: by A's id (403) or by its own (404 site_connection_not_found).
        Assert.Equal(HttpStatusCode.Forbidden, (await adminOfB.GetAsync($"/api/orgs/{orgA}/site-connections")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminOfB.DeleteAsync($"/api/orgs/{orgA}/site-connections/{connectionA}")).StatusCode);
        var notFound = await adminOfB.DeleteAsync($"/api/orgs/{orgB}/site-connections/{connectionA}");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(
            SiteConnectionErrorCodes.NotFound,
            JsonDocument.Parse(await notFound.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        // A's credential disconnects A only, whatever org the request names: B's connection of the same address stays listed and working.
        Assert.Equal(HttpStatusCode.NoContent, (await DisconnectAsync(credentialA, $"?orgId={orgB}")).StatusCode);
        var listedForB = await adminOfB.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgB}/site-connections");
        Assert.Equal(connectionB, Assert.Single(listedForB.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await DisconnectAsync(credentialA)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DisconnectAsync(credentialB)).StatusCode);
    }

    [Fact]
    public async Task Reconnect_TheSameSite_TheOlderCredentialIs401_TheNewerWorks()
    {
        var (orgId, adminId) = await SeedAgencyAsync(flagsOn);
        var site = NewSite();
        var (older, _) = await ConnectAsync(adminId, orgId, site);

        var (newer, newerId) = await ConnectAsync(adminId, orgId, site);

        Assert.Equal(HttpStatusCode.Unauthorized, (await DisconnectAsync(older)).StatusCode);
        using var admin = flagsOn.CreateAuthenticatedClient(adminId, "PropertyOwner");
        var listed = await admin.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/site-connections");
        Assert.Equal(newerId, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await DisconnectAsync(newer)).StatusCode);
    }

    [Fact]
    public async Task Authorize_AnotherOrgsAdmin403_ACollaborator403_Anonymous401_ARuleBroken400InTheLanguageOfTheRequest()
    {
        var (orgA, adminA) = await SeedAgencyAsync(flagsOn);
        var (_, adminB) = await SeedAgencyAsync(flagsOn);
        var site = NewSite();
        var (_, challenge) = Pkce();
        var path = $"/api/orgs/{orgA}/site-connections/authorize";

        using var otherAdmin = flagsOn.CreateAuthenticatedClient(adminB, "PropertyOwner");
        Assert.Equal(HttpStatusCode.Forbidden, (await otherAdmin.PostAsJsonAsync(path, AuthorizeBody(site, challenge))).StatusCode);
        using var collaborator = flagsOn.CreateAuthenticatedClient(adminA, "Staff");
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.PostAsJsonAsync(path, AuthorizeBody(site, challenge))).StatusCode);
        using var anonymous = flagsOn.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, AuthorizeBody(site, challenge))).StatusCode);

        using var admin = flagsOn.CreateAuthenticatedClient(adminA, "PropertyOwner");
        foreach (var (language, words) in new[] { ("it", "non è valida"), ("en", "is not valid") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(AuthorizeBody(site, challenge, returnUrl: "https://evil.example/wp-admin/options-general.php?page=casazen")),
            };
            request.Headers.AcceptLanguage.ParseAdd(language);

            var response = await admin.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            var problem = JsonDocument.Parse(text).RootElement;
            Assert.Equal(SiteConnectionErrorCodes.InvalidRequest, problem.GetProperty("code").GetString());
            Assert.Contains(words, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("evil.example", text, StringComparison.Ordinal);
        }

        using var scope = flagsOn.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.SiteConnectionCodes.IgnoreQueryFilters().CountAsync(c => c.OrgId == orgA));
    }

    [Fact]
    public async Task Token_PastTwentyInTenMinutesFromOneAddress_429_AnotherAddressIsNotLimited()
    {
        var peer = NextPeer();
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync("not-a-code", "not-a-verifier", "https://www.example.test", peer)).StatusCode);

        var limited = await TokenAsync("not-a-code", "not-a-verifier", "https://www.example.test", peer);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync("not-a-code", "not-a-verifier", "https://www.example.test")).StatusCode);
    }

    [Fact]
    public async Task FlagOff_EveryNewEndpoint_404_AndNothingIsStored()
    {
        var ownerId = $"auth0|site-connect-off-{Guid.NewGuid():N}";
        var org = await flagsOff.SeedOrgForOwnerAsync(ownerId);
        var (_, challenge) = Pkce();
        using var admin = flagsOff.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        using var anonymous = flagsOff.CreateClient();
        using var plugin = flagsOff.CreateClient();
        plugin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SiteConnectionTokens.NewCredential());

        HttpResponseMessage[] answers =
        [
            await admin.PostAsJsonAsync($"/api/orgs/{org.Id}/site-connections/authorize", AuthorizeBody(NewSite(), challenge)),
            await admin.GetAsync($"/api/orgs/{org.Id}/site-connections"),
            await admin.DeleteAsync($"/api/orgs/{org.Id}/site-connections/{Guid.NewGuid()}"),
            await anonymous.PostAsJsonAsync("/api/public/site-connections/token", new { code = SiteConnectionTokens.NewCode(), codeVerifier = Pkce().Verifier, siteUrl = NewSite() }),
            await plugin.DeleteAsync("/api/site/v1/connection"),
        ];

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode));
        using var scope = flagsOff.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.SiteConnectionCodes.IgnoreQueryFilters().CountAsync(c => c.OrgId == org.Id));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static async Task<(Guid OrgId, string AdminId)> SeedAgencyAsync(CasazenWebApplicationFactory factory)
    {
        var adminId = $"auth0|site-connect-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(adminId);
        return (org.Id, adminId);
    }

    private static string NewSite() => $"https://www.{Guid.NewGuid():N}.example";

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, SiteConnectionTokens.S256(verifier));
    }

    private static object AuthorizeBody(string site, string challenge, string? returnUrl = null) => new
    {
        siteUrl = site,
        returnUrl = returnUrl ?? site + "/wp-admin/options-general.php?page=casazen",
        state = "state-" + Guid.NewGuid().ToString("N"),
        codeChallenge = challenge,
        codeChallengeMethod = SiteConnectionRules.CodeChallengeMethod,
    };

    private static async Task<string> AuthorizeAsync(HttpClient admin, Guid orgId, string site, string challenge)
    {
        var authorized = await admin.PostAsJsonAsync($"/api/orgs/{orgId}/site-connections/authorize", AuthorizeBody(site, challenge));
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        var redirect = (await authorized.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("redirectUrl").GetString()!;
        return QueryHelpers.ParseQuery(new Uri(redirect).Query)[SiteConnectionRules.CodeQueryParameter].ToString();
    }

    private async Task<(string Credential, Guid ConnectionId)> ConnectAsync(string adminId, Guid orgId, string site)
    {
        var (verifier, challenge) = Pkce();
        using var admin = flagsOn.CreateAuthenticatedClient(adminId, "PropertyOwner");
        var code = await AuthorizeAsync(admin, orgId, site, challenge);
        var exchanged = await TokenAsync(code, verifier, site);
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var grant = await exchanged.Content.ReadFromJsonAsync<JsonElement>();
        return (grant.GetProperty("accessToken").GetString()!, grant.GetProperty("connectionId").GetGuid());
    }

    /// <summary>The exchange from a client address of its own (a new one unless <paramref name="peer"/> is given), as the server of WordPress calls it.</summary>
    private async Task<HttpResponseMessage> TokenAsync(string code, string verifier, string site, string? peer = null)
    {
        using var client = flagsOn.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/site-connections/token")
        {
            Content = JsonContent.Create(new { code, codeVerifier = verifier, siteUrl = site }),
        };
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, peer ?? NextPeer());
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DisconnectAsync(string credential, string query = "")
    {
        using var client = flagsOn.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/site/v1/connection" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await client.SendAsync(request);
    }

    private async Task ExpireAsync(string code)
    {
        using var scope = flagsOn.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = SiteConnectionTokens.Hash(code);
        var row = await db.SiteConnectionCodes.IgnoreQueryFilters().SingleAsync(c => c.CodeHash == hash);
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private static string NextPeer()
    {
        var n = Interlocked.Increment(ref _nextPeer);
        return $"198.19.{n / 250}.{(n % 250) + 1}";
    }

    /// <summary>The integration host with <c>SiteEmbed</c> on and client addresses from <see cref="TestPeerIpStartupFilter"/>; the limits of production.</summary>
    public sealed class ConnectFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Features:SiteEmbed"] = "true",
                }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, TestPeerIpStartupFilter>());
        }
    }
}
