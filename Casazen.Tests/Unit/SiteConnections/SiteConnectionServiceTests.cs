using System.Buffers.Text;
using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Casazen.Tests.Unit.SiteConnections;

/// <summary>
/// S022 (PLG-BE-CONNECT, site integration plan §10.3-§10.4, NF-201, NF-203, NF-204) on the in-memory database: the one-time code, its exchange with PKCE
/// <c>S256</c> for a credential shown once, the same refusal for every failure, the newest connection of a site revoking the older one, the console's list
/// and revocation of the org's own connections only, the checks of a credential, and the proofs that the database holds only hashes and no log line a secret.
/// The race of two exchanges of one code needs the conditional update of PostgreSQL: <c>SiteConnectionPostgresTests</c>.
/// </summary>
public sealed class SiteConnectionServiceTests : IDisposable
{
    private const string Site = "https://www.quokka360.com";
    private const string ReturnUrl = "https://www.quokka360.com/wp-admin/options-general.php?page=casazen";
    private const string AdminId = "auth0|billing-admin-of-quokka";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
    private readonly List<string> _logs = [];
    private readonly Guid _orgA;
    private readonly Guid _orgB;

    public SiteConnectionServiceTests()
    {
        _orgA = SeedOrg("quokka360", "Quokka360");
        _orgB = SeedOrg("altra-agenzia", "Altra Agenzia");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task AuthorizeAsync_ABillingAdminsRequest_GivesTheReturnAddressWithItsQueryTheCodeAndTheState_AndStoresOnlyTheHash()
    {
        var (_, challenge) = Pkce();

        var authorization = await Service().AuthorizeAsync(_orgA, AdminId, Site + "/", ReturnUrl, "state-1", challenge, "S256");

        Assert.NotNull(authorization);
        Assert.Equal(Site, authorization.SiteUrl);
        Assert.Equal(Now() + SiteConnectionRules.CodeLifetime, authorization.ExpiresAt);
        Assert.StartsWith(ReturnUrl + "&casazen_code=", authorization.RedirectUrl, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(new Uri(authorization.RedirectUrl).Query);
        Assert.Equal("casazen", query["page"].ToString());
        Assert.Equal("state-1", query["state"].ToString());
        var code = query["casazen_code"].ToString();
        Assert.True(SiteConnectionTokens.IsCode(code));

        var row = Assert.Single(await _db.SiteConnectionCodes.AsNoTracking().ToListAsync());
        Assert.Equal(_orgA, row.OrgId);
        Assert.Equal(AdminId, row.CreatedByUserId);
        Assert.Equal(Site, row.SiteUrl);
        Assert.Equal(SiteConnectionTokens.Hash(code), row.CodeHash);
        Assert.Equal(challenge, row.CodeChallenge);
        Assert.Null(row.UsedAt);
    }

    public static TheoryData<string, string, string, string, string> RequestsThatBreakARule => new()
    {
        { Site, "https://evil.example/wp-admin/options-general.php?page=casazen", "state-1", ValidChallenge, "S256" },
        { Site, "javascript:alert(document.cookie)", "state-1", ValidChallenge, "S256" },
        { Site, "ftp://www.quokka360.com/wp-admin/", "state-1", ValidChallenge, "S256" },
        { Site, "https://admin:secret@www.quokka360.com/wp-admin/", "state-1", ValidChallenge, "S256" },
        { Site, ReturnUrl + "#casazen", "state-1", ValidChallenge, "S256" },
        { "http://www.quokka360.com", "http://www.quokka360.com/wp-admin/", "state-1", ValidChallenge, "S256" },
        { Site + "/wordpress", ReturnUrl, "state-1", ValidChallenge, "S256" },
        { Site + "?page=casazen", ReturnUrl, "state-1", ValidChallenge, "S256" },
        { "javascript:alert(1)", ReturnUrl, "state-1", ValidChallenge, "S256" },
        { Site, ReturnUrl, "state-1", ValidChallenge, "plain" },
        { Site, ReturnUrl, "state-1", "not-a-challenge", "S256" },
        { Site, ReturnUrl, "bad state!", ValidChallenge, "S256" },
        { Site, ReturnUrl, string.Empty, ValidChallenge, "S256" },
    };

    [Theory]
    [MemberData(nameof(RequestsThatBreakARule))]
    public async Task AuthorizeAsync_ARequestThatBreaksARule_IsRefused_AndStoresNothing(
        string siteUrl, string returnUrl, string state, string challenge, string method)
    {
        var authorization = await Service().AuthorizeAsync(_orgA, AdminId, siteUrl, returnUrl, state, challenge, method);

        Assert.Null(authorization);
        Assert.Empty(await _db.SiteConnectionCodes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AuthorizeAsync_DeletesTheCodesExpiredForMoreThanADay_AndKeepsTheOthers()
    {
        var old = new SiteConnectionCode
        {
            OrgId = _orgB,
            CreatedByUserId = "auth0|other",
            SiteUrl = "https://altra.example",
            CodeHash = new string('a', 64),
            CodeChallenge = ValidChallenge,
            ExpiresAt = Now().AddDays(-2),
            CreatedAt = Now().AddDays(-2).AddMinutes(-5),
        };
        var recent = new SiteConnectionCode
        {
            OrgId = _orgA,
            CreatedByUserId = AdminId,
            SiteUrl = Site,
            CodeHash = new string('b', 64),
            CodeChallenge = ValidChallenge,
            ExpiresAt = Now().AddHours(-1),
            CreatedAt = Now().AddHours(-1).AddMinutes(-5),
        };
        _db.SiteConnectionCodes.AddRange(old, recent);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await Service().AuthorizeAsync(_orgA, AdminId, Site, ReturnUrl, "state-1", ValidChallenge, "S256");

        var left = await _db.SiteConnectionCodes.AsNoTracking().Select(c => c.Id).ToListAsync();
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(old.Id, left);
        Assert.Contains(recent.Id, left);
    }

    [Fact]
    public async Task ExchangeAsync_TheRightCodeVerifierAndSite_GivesACredentialOnce_AndStoresOnlyItsHash()
    {
        var (verifier, challenge) = Pkce();
        var code = await AuthorizeAsync(_orgA, challenge);

        var grant = await Service().ExchangeAsync(code, verifier, Site);

        Assert.NotNull(grant);
        Assert.True(SiteConnectionTokens.IsCredential(grant.AccessToken));
        Assert.Equal(new SiteConnectionOrg(_orgA, "quokka360", "Quokka360"), grant.Org);
        var connection = Assert.Single(await _db.SiteConnections.AsNoTracking().ToListAsync());
        Assert.Equal(grant.ConnectionId, connection.Id);
        Assert.Equal(_orgA, connection.OrgId);
        Assert.Equal(Site, connection.SiteUrl);
        Assert.Equal(AdminId, connection.CreatedByUserId);
        Assert.Equal(SiteConnectionTokens.Hash(grant.AccessToken), connection.TokenHash);
        Assert.Null(connection.RevokedAt);
        Assert.Equal(Now(), (await _db.SiteConnectionCodes.AsNoTracking().SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task ExchangeAsync_UsedExpiredWrongVerifierOtherSiteUnknownOrMalformed_AreAllTheSameRefusal()
    {
        var (verifier, challenge) = Pkce();
        var used = await AuthorizeAsync(_orgA, challenge);
        Assert.NotNull(await Service().ExchangeAsync(used, verifier, Site));

        var wrongVerifier = await AuthorizeAsync(_orgA, challenge);
        var otherSite = await AuthorizeAsync(_orgA, challenge);
        var expired = await AuthorizeAsync(_orgA, challenge);

        Assert.Null(await Service().ExchangeAsync(used, verifier, Site));
        Assert.Null(await Service().ExchangeAsync(wrongVerifier, Pkce().Verifier, Site));
        Assert.Null(await Service().ExchangeAsync(otherSite, verifier, "https://other.example"));
        Assert.Null(await Service().ExchangeAsync(SiteConnectionTokens.NewCode(), verifier, Site));
        Assert.Null(await Service().ExchangeAsync("not a code", verifier, Site));
        Assert.Null(await Service().ExchangeAsync(null, null, null));
        _clock.Advance(SiteConnectionRules.CodeLifetime + TimeSpan.FromSeconds(1));
        Assert.Null(await Service().ExchangeAsync(expired, verifier, Site));

        Assert.Null(await Service().ExchangeAsync(wrongVerifier, verifier, Site));
        Assert.Null(await Service().ExchangeAsync(otherSite, verifier, Site));
        Assert.Single(await _db.SiteConnections.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ExchangeAsync_TheSameSiteAgain_RevokesThePreviousCredential_AnotherSiteKeepsItsOwn()
    {
        var first = await ConnectAsync(_orgA, Site);
        var otherSite = await ConnectAsync(_orgA, "https://blog.quokka360.com");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var second = await ConnectAsync(_orgA, Site);

        Assert.Null(await Service().AuthenticateAsync(first.AccessToken));
        Assert.Equal(new SiteConnectionPrincipal(second.ConnectionId, _orgA), await Service().AuthenticateAsync(second.AccessToken));
        Assert.Equal(new SiteConnectionPrincipal(otherSite.ConnectionId, _orgA), await Service().AuthenticateAsync(otherSite.AccessToken));
        var revoked = await _db.SiteConnections.AsNoTracking().SingleAsync(c => c.Id == first.ConnectionId);
        Assert.Equal(Now(), revoked.RevokedAt);
    }

    [Fact]
    public async Task Storage_HoldsOnlyHashes_AndNoLogLineHoldsASecret()
    {
        var (verifier, challenge) = Pkce();
        var authorization = await Service().AuthorizeAsync(_orgA, AdminId, Site, ReturnUrl, "state-secret-1", challenge, "S256");
        var code = QueryHelpers.ParseQuery(new Uri(authorization!.RedirectUrl).Query)["casazen_code"].ToString();
        var grant = await Service().ExchangeAsync(code, verifier, Site);
        Assert.NotNull(grant);
        Assert.Null(await Service().ExchangeAsync(code, verifier, Site));
        var (otherVerifier, otherChallenge) = Pkce();
        var refused = await AuthorizeAsync(_orgA, otherChallenge);
        Assert.Null(await Service().ExchangeAsync(refused, verifier, Site));
        Assert.Null(await Service().AuthenticateAsync(SiteConnectionTokens.NewCredential()));
        Assert.NotNull(await Service().AuthenticateAsync(grant.AccessToken));
        Assert.True(await Service().RevokeAsync(_orgA, grant.ConnectionId));

        string[] secrets = [code, refused, grant.AccessToken, verifier, otherVerifier];
        var stored = await StoredTextAsync();
        foreach (var secret in secrets)
            Assert.DoesNotContain(stored, value => value.Contains(secret, StringComparison.Ordinal));

        Assert.NotEmpty(_logs);
        string[] neverLogged = [.. secrets, challenge, otherChallenge, "state-secret-1", "page=casazen", AdminId];
        foreach (var line in _logs)
        {
            foreach (var secret in neverLogged)
                Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        }

        Assert.Contains(_logs, line => line.Contains("www.quokka360.com", StringComparison.Ordinal) && line.Contains(_orgA.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListAsync_OnlyTheActiveConnectionsOfTheOrg_TheNewestFirst()
    {
        var revoked = await ConnectAsync(_orgA, "https://old.quokka360.com");
        Assert.True(await Service().RevokeAsync(_orgA, revoked.ConnectionId));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var older = await ConnectAsync(_orgA, Site);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await ConnectAsync(_orgA, "https://blog.quokka360.com");
        await ConnectAsync(_orgB, "https://altra.example");

        var list = await Service().ListAsync(_orgA);

        Assert.Equal([newer.ConnectionId, older.ConnectionId], list.Select(c => c.Id));
        Assert.Equal(["https://blog.quokka360.com", Site], list.Select(c => c.SiteUrl));
    }

    [Fact]
    public async Task RevokeAsync_AConnectionOfAnotherOrg_IsNotFound_AndStaysActive()
    {
        var ofA = await ConnectAsync(_orgA, Site);

        Assert.False(await Service().RevokeAsync(_orgB, ofA.ConnectionId));
        Assert.False(await Service().RevokeAsync(_orgA, Guid.NewGuid()));
        Assert.NotNull(await Service().AuthenticateAsync(ofA.AccessToken));

        Assert.True(await Service().RevokeAsync(_orgA, ofA.ConnectionId));
        Assert.True(await Service().RevokeAsync(_orgA, ofA.ConnectionId));
        Assert.Null(await Service().AuthenticateAsync(ofA.AccessToken));
        Assert.Empty(await Service().ListAsync(_orgA));
    }

    [Fact]
    public async Task RevokeAsync_ThePluginsOwnConnection_StopsItsCredentialOnly()
    {
        var own = await ConnectAsync(_orgA, Site);
        var ofB = await ConnectAsync(_orgB, "https://altra.example");

        Assert.True(await Service().RevokeAsync(own.ConnectionId));

        Assert.Null(await Service().AuthenticateAsync(own.AccessToken));
        Assert.Equal(new SiteConnectionPrincipal(ofB.ConnectionId, _orgB), await Service().AuthenticateAsync(ofB.AccessToken));
        Assert.False(await Service().RevokeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AuthenticateAsync_RecordsTheUseAtMostOnceEveryFiveMinutes()
    {
        var grant = await ConnectAsync(_orgA, Site);
        var first = Now();

        Assert.NotNull(await Service().AuthenticateAsync(grant.AccessToken));
        Assert.Equal(first, await LastUsedAsync(grant.ConnectionId));

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.NotNull(await Service().AuthenticateAsync(grant.AccessToken));
        Assert.Equal(first, await LastUsedAsync(grant.ConnectionId));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await Service().AuthenticateAsync(grant.AccessToken));
        Assert.Equal(Now(), await LastUsedAsync(grant.ConnectionId));
    }

    [Fact]
    public async Task AuthenticateAsync_UnknownMalformedOrOfAnOrgNoLongerActive_OpensNothing()
    {
        var grant = await ConnectAsync(_orgA, Site);

        Assert.Null(await Service().AuthenticateAsync(null));
        Assert.Null(await Service().AuthenticateAsync("czs_short"));
        Assert.Null(await Service().AuthenticateAsync(grant.AccessToken[SiteConnectionTokens.CredentialPrefix.Length..]));
        Assert.Null(await Service().AuthenticateAsync(SiteConnectionTokens.NewCredential()));

        var (verifier, challenge) = Pkce();
        var code = await AuthorizeAsync(_orgA, challenge);
        var org = await _db.Orgs.SingleAsync(o => o.Id == _orgA);
        org.IsActive = false;
        await _db.SaveChangesAsync();

        Assert.Null(await Service().AuthenticateAsync(grant.AccessToken));
        Assert.Null(await Service().ExchangeAsync(code, verifier, Site));
        Assert.Single(await _db.SiteConnections.AsNoTracking().ToListAsync());
    }

    private static readonly string ValidChallenge = SiteConnectionTokens.S256(new string('v', 43));

    private SiteConnectionService Service() => new(_db, _clock, new ListLogger<SiteConnectionService>(_logs));

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, SiteConnectionTokens.S256(verifier));
    }

    private async Task<string> AuthorizeAsync(Guid orgId, string challenge, string site = Site)
    {
        var authorization = await Service().AuthorizeAsync(orgId, AdminId, site, site + "/wp-admin/options-general.php?page=casazen", "state-1", challenge, "S256");
        Assert.NotNull(authorization);
        return QueryHelpers.ParseQuery(new Uri(authorization.RedirectUrl).Query)[SiteConnectionRules.CodeQueryParameter].ToString();
    }

    private async Task<SiteConnectionGrant> ConnectAsync(Guid orgId, string site)
    {
        var (verifier, challenge) = Pkce();
        var code = await AuthorizeAsync(orgId, challenge, site);
        var grant = await Service().ExchangeAsync(code, verifier, site);
        Assert.NotNull(grant);
        return grant;
    }

    private async Task<DateTime?> LastUsedAsync(Guid connectionId) =>
        (await _db.SiteConnections.AsNoTracking().SingleAsync(c => c.Id == connectionId)).LastUsedAt;

    private async Task<List<string>> StoredTextAsync()
    {
        var codes = await _db.SiteConnectionCodes.AsNoTracking().ToListAsync();
        var connections = await _db.SiteConnections.AsNoTracking().ToListAsync();
        return
        [
            .. codes.SelectMany(c => new[] { c.CreatedByUserId, c.SiteUrl, c.CodeHash, c.CodeChallenge }),
            .. connections.SelectMany(c => new[] { c.SiteUrl, c.TokenHash, c.CreatedByUserId }),
        ];
    }

    private Guid SeedOrg(string slug, string name)
    {
        var org = new OrgEntity { Name = name, DisplayName = name, Slug = slug, ContactEmail = $"info@{slug}.example", IsActive = true };
        _db.Orgs.Add(org);
        _db.SaveChanges();
        return org.Id;
    }

    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
