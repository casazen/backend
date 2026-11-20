using System.Security.Claims;
using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E40/F01/US01/T02 (NF-140, AC5): the second wall of the owner portal on an agency domain. CORS cannot know whose token a call carries, so the
/// <c>OwnerPortal</c> policy refuses an owner token used from the domain of an agency the owner does not belong to; a call with no <c>Origin</c>,
/// from a platform origin or from the owner's own agency passes as before.
/// </summary>
public class OwnerPortalHostOriginTests
{
    private const string Namespace = "https://casazen.app";
    private const string PlatformOrigin = "https://app.casazen.test";
    private static readonly Guid AgencyA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid AgencyB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly Mock<IPublicHostResolver> _resolver = new();

    public OwnerPortalHostOriginTests()
    {
        _resolver.Setup(r => r.ResolveAsync("prenota.agenzia-a.it", It.IsAny<CancellationToken>())).ReturnsAsync(Host(AgencyA));
        _resolver.Setup(r => r.ResolveAsync("prenota.agenzia-b.it", It.IsAny<CancellationToken>())).ReturnsAsync(Host(AgencyB));
    }

    [Fact]
    public async Task OriginOfAnotherOrg_Forbidden()
    {
        var context = await AuthorizeAsync(origin: "https://prenota.agenzia-b.it", agencies: [AgencyA]);

        Assert.False(context.HasSucceeded);
        Assert.Contains(context.FailureReasons, r => r.Message == OwnerPortalHostOriginAuthorizationHandler.OtherAgencyReason);
    }

    [Fact]
    public async Task OwnOrgOrigin_Allowed()
    {
        var context = await AuthorizeAsync(origin: "https://prenota.agenzia-a.it", agencies: [AgencyB, AgencyA]);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task NoOrigin_Allowed()
    {
        // The mobile app and the server calls send no Origin: nothing changes for them.
        var context = await AuthorizeAsync(origin: null, agencies: [AgencyA]);

        Assert.True(context.HasSucceeded);
        _resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlatformOrigin_Allowed_WithoutAskingTheResolver()
    {
        var context = await AuthorizeAsync(origin: PlatformOrigin, agencies: [AgencyA]);

        Assert.True(context.HasSucceeded);
        _resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OwnerWithoutAgencies_FromAnAgencyDomain_Forbidden()
    {
        var context = await AuthorizeAsync(origin: "https://prenota.agenzia-a.it", agencies: []);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task OriginThatIsNoOrgHost_Unchanged()
    {
        // Not an org host (the browser's CORS refuses it anyway): this wall adds nothing, as for a call with no Origin.
        _resolver.Setup(r => r.ResolveAsync("unknown.example.test", It.IsAny<CancellationToken>())).ReturnsAsync((ResolveHostResponseDto?)null);

        var context = await AuthorizeAsync(origin: "https://unknown.example.test", agencies: [AgencyA]);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task OwnerPortalPolicy_AsTheAppRegistersIt_HasTheSecondWallAsARequirementOfItsOwn()
    {
        // The real registration (AddCasazenAuthorization): the policy carries the requirement next to the first one, and its handler is registered, so the second wall
        // cannot be dropped without a test failing (a handler tried alone would not notice).
        var registered = new ServiceCollection();
        registered.AddLogging();
        registered.AddCasazenAuthorization();

        var policy = await registered.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.OwnerPortal);

        Assert.NotNull(policy);
        Assert.Single(policy.Requirements.OfType<OwnerPortalRequirement>());
        Assert.Single(policy.Requirements.OfType<OwnerPortalHostOriginRequirement>());
        Assert.Contains(registered, d => d.ServiceType == typeof(IAuthorizationHandler) && d.ImplementationType == typeof(OwnerPortalHostOriginAuthorizationHandler));
    }

    [Theory]
    [InlineData("https://prenota.agenzia-b.it", false)]
    [InlineData("https://prenota.agenzia-a.it", true)]
    [InlineData(PlatformOrigin, true)]
    [InlineData(null, true)]
    public async Task OwnerPortalPolicy_TheRegisteredPolicyWithBothHandlers_OwnerOfAgencyA(string? origin, bool allowed)
    {
        // The policy as the app registers it, evaluated by the authorization service with the first handler and the second wall: the owner of A is refused from the domain
        // of B (the 403), and passes from its own domain, from the platform and with no Origin. A real token: one claim per agency.
        var registered = new ServiceCollection();
        registered.AddLogging();
        registered.AddCasazenAuthorization();
        var policy = await registered.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.OwnerPortal);
        Assert.NotNull(policy);

        var http = new DefaultHttpContext();
        if (origin is not null)
            http.Request.Headers.Origin = origin;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddSingleton<IAuthorizationHandler>(new OwnerPortalAuthorizationHandler(Options.Create(new Auth0Options { CustomClaimNamespace = Namespace })));
        services.AddSingleton<IAuthorizationHandler>(NewHandler(http));
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(OwnerOf([AgencyA.ToString()]), http, policy);

        Assert.Equal(allowed, result.Succeeded);
    }

    [Theory]
    [InlineData("https://prenota.agenzia-a.it")]
    [InlineData("https://made-up-1.example.test")]
    public async Task Unauthenticated_WithAnOrigin_IsNotLookedUp_AndTheRequirementIsNotMet(string origin)
    {
        // The first requirement of the policy refuses a caller nobody authenticated (401): the second wall does not ask the resolver, a database lookup per made-up
        // Origin that would run before the rate limiter.
        var context = await AuthorizeAsync(origin: origin, agencyClaims: [], authenticated: false);

        Assert.False(context.HasSucceeded);
        Assert.False(context.HasFailed);
        _resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OwnerOfSeveralAgencies_OneClaimPerAgency_OriginOfTheSecond_Allowed()
    {
        // A real token carries the array agency_ids as one claim per agency: the owner of A and B is at home on the domain of B, which is not the first claim.
        var context = await AuthorizeAsync(origin: "https://prenota.agenzia-b.it", agencyClaims: [AgencyA.ToString(), AgencyB.ToString()]);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task OwnerOfSeveralAgencies_OneClaimPerAgency_OriginOfAnotherOrg_Forbidden()
    {
        var other = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");
        _resolver.Setup(r => r.ResolveAsync("prenota.agenzia-c.it", It.IsAny<CancellationToken>())).ReturnsAsync(Host(other));

        var context = await AuthorizeAsync(origin: "https://prenota.agenzia-c.it", agencyClaims: [AgencyA.ToString(), AgencyB.ToString()]);

        Assert.False(context.HasSucceeded);
        Assert.Contains(context.FailureReasons, r => r.Message == OwnerPortalHostOriginAuthorizationHandler.OtherAgencyReason);
    }

    [Fact]
    public void AgencyIds_EveryShapeOfTheClaim_GiveTheSameIds()
    {
        // One claim per agency (the array of Auth0 as the JWT handler gives it), the JSON text of the array, a comma-separated list and one id: the same two agencies.
        string[][] shapes =
        [
            [AgencyA.ToString(), AgencyB.ToString()],
            [System.Text.Json.JsonSerializer.Serialize(new[] { AgencyA.ToString(), AgencyB.ToString() })],
            [$"{AgencyA}, {AgencyB}"],
            [AgencyA.ToString(), $"[\"{AgencyB}\"]", AgencyA.ToString()],
        ];

        foreach (var shape in shapes)
        {
            var ids = OwnerOf(shape).GetOwnerAgencyIds(Namespace);

            Assert.Equal(2, ids.Count);
            Assert.Contains(AgencyA, ids);
            Assert.Contains(AgencyB, ids);
        }

        Assert.Equal(new[] { AgencyA }, OwnerOf([AgencyA.ToString()]).GetOwnerAgencyIds(Namespace));
        Assert.Empty(OwnerOf(["not-an-id", "[broken"]).GetOwnerAgencyIds(Namespace));
        Assert.Empty(OwnerOf([]).GetOwnerAgencyIds(Namespace));
    }

    /// <summary>The claim the way these tests always wrote it: ONE claim with the JSON text of the array (the shape of the other tests of the owner portal).</summary>
    private Task<AuthorizationHandlerContext> AuthorizeAsync(string? origin, Guid[] agencies)
    {
        return AuthorizeAsync(origin, [System.Text.Json.JsonSerializer.Serialize(agencies.Select(a => a.ToString()))]);
    }

    /// <summary>One claim for each value of <paramref name="agencyClaims"/>: the shape of a real token, where the array of Auth0 becomes one claim per agency (as the roles do).</summary>
    private async Task<AuthorizationHandlerContext> AuthorizeAsync(string? origin, string[] agencyClaims, bool authenticated = true)
    {
        var http = new DefaultHttpContext();
        if (origin is not null)
            http.Request.Headers.Origin = origin;

        var context = new AuthorizationHandlerContext([new OwnerPortalHostOriginRequirement()], OwnerOf(agencyClaims, authenticated), http);
        await NewHandler(http).HandleAsync(context);
        return context;
    }

    private static ClaimsPrincipal OwnerOf(string[] agencyClaims, bool authenticated = true)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "auth0|owner"),
            new($"{Namespace}/context", "owner_portal"),
            new($"{Namespace}/owner_id", Guid.NewGuid().ToString()),
        };
        claims.AddRange(agencyClaims.Select(value => new Claim($"{Namespace}/agency_ids", value)));

        // An identity with no authentication type is not authenticated.
        return new ClaimsPrincipal(authenticated ? new ClaimsIdentity(claims, "TestAuth") : new ClaimsIdentity(claims));
    }

    private OwnerPortalHostOriginAuthorizationHandler NewHandler(HttpContext http)
    {
        var platformOrigins = new CorsOriginAllowList(new CorsOriginOptions { AllowedOrigins = { PlatformOrigin } }, allowLoopback: false);
        return new OwnerPortalHostOriginAuthorizationHandler(
            new HttpContextAccessor { HttpContext = http },
            platformOrigins,
            _resolver.Object,
            Options.Create(new Auth0Options { CustomClaimNamespace = Namespace }));
    }

    private static ResolveHostResponseDto Host(Guid orgId) => new()
    {
        OrgId = orgId,
        Slug = $"agency-{orgId:N}",
        PublicHostMode = PublicHostMode.CustomDomain,
        Surfaces = new ResolveHostSurfacesDto { GuestArea = true, OwnerPortal = true },
        OwnerLoginReady = true,
    };
}
