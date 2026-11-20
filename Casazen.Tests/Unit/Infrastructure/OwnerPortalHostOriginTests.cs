using System.Security.Claims;
using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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

    private async Task<AuthorizationHandlerContext> AuthorizeAsync(string? origin, Guid[] agencies)
    {
        var http = new DefaultHttpContext();
        if (origin is not null)
            http.Request.Headers.Origin = origin;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "auth0|owner"),
            new($"{Namespace}/context", "owner_portal"),
            new($"{Namespace}/owner_id", Guid.NewGuid().ToString()),
            new($"{Namespace}/agency_ids", System.Text.Json.JsonSerializer.Serialize(agencies.Select(a => a.ToString()))),
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var context = new AuthorizationHandlerContext([new OwnerPortalHostOriginRequirement()], user, http);

        var handler = new OwnerPortalHostOriginAuthorizationHandler(
            new HttpContextAccessor { HttpContext = http },
            new CorsOriginAllowList(new CorsOriginOptions { AllowedOrigins = { PlatformOrigin } }, allowLoopback: false),
            _resolver.Object,
            Options.Create(new Auth0Options { CustomClaimNamespace = Namespace }));
        await handler.HandleAsync(context);
        return context;
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
