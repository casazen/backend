using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.SiteConnections;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.SiteConnections;

/// <summary>
/// S022 (PLG-BE-CONNECT, site integration plan §10.3, AC 1, 3, 6 and 7) at the level of the controllers, with the real service on the in-memory database: the
/// IDOR check of the console (another org's id is 403, no org is 404, nothing stored), the one 400 of each refusal, the whole flow from the confirmation to
/// the revocation, the revocation of another org's connection (404 <c>site_connection_not_found</c>), and the plugin disconnecting itself. The pipeline (the
/// schemes, the flag, the limits) is proved over HTTP by <c>SiteConnectionIntegrationTests</c>.
/// </summary>
public sealed class SiteConnectionEndpointsTests : IDisposable
{
    private const string Site = "https://www.quokka360.com";
    private const string ReturnUrl = "https://www.quokka360.com/wp-admin/options-general.php?page=casazen";
    private const string AdminId = "auth0|billing-admin-of-quokka";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
    private readonly Guid _orgA;
    private readonly Guid _orgB;

    public SiteConnectionEndpointsTests()
    {
        _orgA = SeedOrg("quokka360", "Quokka360");
        _orgB = SeedOrg("altra-agenzia", "Altra Agenzia");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Console_TheIdOfAnotherOrg_403_WithoutAnOrg_404_AndNothingIsStored()
    {
        var connection = await ConnectAsync(_orgA);
        var otherOrgsAdmin = Console(callerOrgId: _orgB);
        var noOrg = Console(callerOrgId: null);

        AssertProblem((await otherOrgsAdmin.Authorize(_orgA, AuthorizeRequest(Pkce().Challenge), CancellationToken.None)).Result, 403, ProblemCodes.Forbidden);
        AssertProblem((await otherOrgsAdmin.List(_orgA, CancellationToken.None)).Result, 403, ProblemCodes.Forbidden);
        AssertProblem(await otherOrgsAdmin.Revoke(_orgA, connection.ConnectionId, CancellationToken.None), 403, ProblemCodes.Forbidden);
        AssertProblem((await noOrg.Authorize(_orgA, AuthorizeRequest(Pkce().Challenge), CancellationToken.None)).Result, 404, ProblemCodes.NotFound);

        Assert.Single(await _db.SiteConnectionCodes.AsNoTracking().ToListAsync());
        Assert.NotNull(await Service().AuthenticateAsync(connection.AccessToken));
    }

    [Fact]
    public async Task Authorize_ARequestThatBreaksARule_400InvalidRequest_WithoutEchoingIt()
    {
        var request = AuthorizeRequest(Pkce().Challenge);
        request.ReturnUrl = "https://evil.example/steal?page=casazen";

        var result = await Console(_orgA).Authorize(_orgA, request, CancellationToken.None);

        var problem = AssertProblem(result.Result, 400, SiteConnectionErrorCodes.InvalidRequest);
        Assert.DoesNotContain("evil.example", JsonSerializer.Serialize(problem), StringComparison.Ordinal);
        Assert.Empty(await _db.SiteConnectionCodes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TheWholeFlow_ConfirmExchangeListRevoke()
    {
        var (verifier, challenge) = Pkce();

        var authorized = Assert.IsType<SiteConnectionAuthorizeResponse>(
            Assert.IsType<OkObjectResult>((await Console(_orgA).Authorize(_orgA, AuthorizeRequest(challenge), CancellationToken.None)).Result).Value);
        Assert.Equal(Site, authorized.SiteUrl);
        Assert.StartsWith(ReturnUrl + "&casazen_code=", authorized.RedirectUrl, StringComparison.Ordinal);
        var code = QueryHelpers.ParseQuery(new Uri(authorized.RedirectUrl).Query)[SiteConnectionRules.CodeQueryParameter].ToString();

        var exchanged = await Exchange().Token(new SiteConnectionTokenRequest { Code = code, CodeVerifier = verifier, SiteUrl = Site }, CancellationToken.None);
        var grant = Assert.IsType<SiteConnectionTokenResponse>(Assert.IsType<OkObjectResult>(exchanged.Result).Value);
        Assert.True(SiteConnectionTokens.IsCredential(grant.AccessToken));
        Assert.Equal(new SiteConnectionOrgDto(_orgA, "quokka360", "Quokka360"), grant.Org);

        var listed = Assert.IsAssignableFrom<IEnumerable<SiteConnectionDto>>(
            Assert.IsType<OkObjectResult>((await Console(_orgA).List(_orgA, CancellationToken.None)).Result).Value);
        var item = Assert.Single(listed);
        Assert.Equal((grant.ConnectionId, Site, (DateTime?)null), (item.Id, item.SiteUrl, item.LastUsedAt));

        Assert.IsType<NoContentResult>(await Console(_orgA).Revoke(_orgA, grant.ConnectionId, CancellationToken.None));
        Assert.Null(await Service().AuthenticateAsync(grant.AccessToken));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<SiteConnectionDto>>(
            Assert.IsType<OkObjectResult>((await Console(_orgA).List(_orgA, CancellationToken.None)).Result).Value));
    }

    [Fact]
    public async Task Token_UsedWrongVerifierOtherSiteUnknownOrMalformed_TheSame400InvalidGrant()
    {
        var (verifier, challenge) = Pkce();
        var used = await AuthorizeAsync(_orgA, challenge);
        Assert.IsType<OkObjectResult>((await Exchange().Token(Exchange(used, verifier), CancellationToken.None)).Result);
        var wrongVerifier = await AuthorizeAsync(_orgA, challenge);
        var otherSite = await AuthorizeAsync(_orgA, challenge);

        SiteConnectionTokenRequest[] refused =
        [
            Exchange(used, verifier),
            Exchange(wrongVerifier, Pkce().Verifier),
            new() { Code = otherSite, CodeVerifier = verifier, SiteUrl = "https://other.example" },
            Exchange(SiteConnectionTokens.NewCode(), verifier),
            new() { Code = "not a code", CodeVerifier = "short", SiteUrl = "javascript:alert(1)" },
            new(),
        ];

        var bodies = new List<string>();
        foreach (var request in refused)
        {
            var problem = AssertProblem((await Exchange().Token(request, CancellationToken.None)).Result, 400, SiteConnectionErrorCodes.InvalidGrant);
            bodies.Add(JsonSerializer.Serialize(new { problem.Status, problem.Title, problem.Detail, problem.Type, Code = problem.Extensions[ApiProblemDetails.CodeExtension] }));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        Assert.Single(await _db.SiteConnections.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Revoke_AConnectionOfAnotherOrg_404SiteConnectionNotFound_AndItStaysActive()
    {
        var ofA = await ConnectAsync(_orgA);

        var result = await Console(_orgB).Revoke(_orgB, ofA.ConnectionId, CancellationToken.None);

        AssertProblem(result, 404, SiteConnectionErrorCodes.NotFound);
        Assert.NotNull(await Service().AuthenticateAsync(ofA.AccessToken));
        var listedForB = Assert.IsAssignableFrom<IEnumerable<SiteConnectionDto>>(
            Assert.IsType<OkObjectResult>((await Console(_orgB).List(_orgB, CancellationToken.None)).Result).Value);
        Assert.Empty(listedForB);
    }

    [Fact]
    public async Task Disconnect_ThePluginsCredential_204_ThenItNoLongerOpens_APersonGets401()
    {
        var grant = await ConnectAsync(_orgA);
        var ofB = await ConnectAsync(_orgB);
        var principal = await Service().AuthenticateAsync(grant.AccessToken);
        Assert.NotNull(principal);

        Assert.IsType<NoContentResult>(await Plugin(SitePrincipal(principal)).Disconnect(CancellationToken.None));

        Assert.Null(await Service().AuthenticateAsync(grant.AccessToken));
        Assert.NotNull(await Service().AuthenticateAsync(ofB.AccessToken));
        var person = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", AdminId), new Claim(ClaimTypes.Role, "PropertyOwner")], "Bearer"));
        Assert.IsType<UnauthorizedResult>(await Plugin(person).Disconnect(CancellationToken.None));
    }

    private SiteConnectionService Service() => new(_db, _clock, NullLogger<SiteConnectionService>.Instance);

    private SiteConnectionsController Console(Guid? callerOrgId)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(callerOrgId);
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", AdminId), new Claim(ClaimTypes.Role, "PropertyOwner")], "Bearer"));
        return new SiteConnectionsController(resolver.Object, Service()) { ControllerContext = Context(admin) };
    }

    private PublicSiteConnectionsController Exchange() =>
        new(Service()) { ControllerContext = Context(new ClaimsPrincipal(new ClaimsIdentity())) };

    private SiteApiController Plugin(ClaimsPrincipal user) =>
        new(Service(), Mock.Of<ISiteAccountBuilder>(), Mock.Of<IOrgDomainService>(), Mock.Of<IStringLocalizer<SharedResources>>())
        {
            ControllerContext = Context(user),
        };

    private static ControllerContext Context(ClaimsPrincipal user) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = user,
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
        },
    };

    private static ClaimsPrincipal SitePrincipal(SiteConnectionPrincipal site) =>
        new(new ClaimsIdentity(
            [
                new Claim(SiteTokenDefaults.ConnectionIdClaim, site.ConnectionId.ToString("D")),
                new Claim(SiteTokenDefaults.OrgIdClaim, site.OrgId.ToString("D")),
                new Claim(SiteTokenDefaults.ScopeClaim, SiteTokenDefaults.DomainScope),
            ],
            SiteTokenDefaults.AuthenticationScheme));

    private static ProblemDetails AssertProblem(IActionResult? result, int status, string code)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(objectResult.Value);
        Assert.Equal(code, problem.Extensions[ApiProblemDetails.CodeExtension]);
        return problem;
    }

    private static SiteConnectionAuthorizeRequest AuthorizeRequest(string challenge) => new()
    {
        SiteUrl = Site,
        ReturnUrl = ReturnUrl,
        State = "state-1",
        CodeChallenge = challenge,
        CodeChallengeMethod = SiteConnectionRules.CodeChallengeMethod,
    };

    private static SiteConnectionTokenRequest Exchange(string code, string verifier) =>
        new() { Code = code, CodeVerifier = verifier, SiteUrl = Site };

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, SiteConnectionTokens.S256(verifier));
    }

    private async Task<string> AuthorizeAsync(Guid orgId, string challenge)
    {
        var authorization = await Service().AuthorizeAsync(orgId, AdminId, Site, ReturnUrl, "state-1", challenge, SiteConnectionRules.CodeChallengeMethod);
        Assert.NotNull(authorization);
        return QueryHelpers.ParseQuery(new Uri(authorization.RedirectUrl).Query)[SiteConnectionRules.CodeQueryParameter].ToString();
    }

    private async Task<SiteConnectionGrant> ConnectAsync(Guid orgId)
    {
        var (verifier, challenge) = Pkce();
        var grant = await Service().ExchangeAsync(await AuthorizeAsync(orgId, challenge), verifier, Site);
        Assert.NotNull(grant);
        return grant;
    }

    private Guid SeedOrg(string slug, string name)
    {
        var org = new OrgEntity { Name = name, DisplayName = name, Slug = slug, ContactEmail = $"info@{slug}.example", IsActive = true };
        _db.Orgs.Add(org);
        _db.SaveChanges();
        return org.Id;
    }
}
