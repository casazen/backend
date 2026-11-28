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
        _resolver.Setup(r => r.ResolveAsync("unknown.example.test", It.IsAny<CancellationToken>())).ReturnsAsync((ResolveHostResponseDto?)null);

        var context = await AuthorizeAsync(origin: "https://unknown.example.test", agencies: [AgencyA]);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task OwnerPortalPolicy_AsTheAppRegistersIt_HasTheSecondWallAsARequirementOfItsOwn()
    {
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
        var context = await AuthorizeAsync(origin: origin, agencyClaims: [], authenticated: false);

        Assert.False(context.HasSucceeded);
        Assert.False(context.HasFailed);
        _resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OwnerOfSeveralAgencies_OneClaimPerAgency_OriginOfTheSecond_Allowed()
    {
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

    private Task<AuthorizationHandlerContext> AuthorizeAsync(string? origin, Guid[] agencies)
    {
        return AuthorizeAsync(origin, [System.Text.Json.JsonSerializer.Serialize(agencies.Select(a => a.ToString()))]);
    }

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
