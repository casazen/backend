using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// AM-03b: <c>GET /api/gdpr/org/export</c> is an act of the holder of the org. It needs the org policy (the owner and the
/// administrators; <c>OrgBillingAdminPolicyCoverageTests</c> and <c>OrgBillingAdminMembershipAccessTests</c> prove that the policy
/// refuses a collaborator, a manager and the other members of a team), not <c>property.read</c> as before, which a collaborator
/// limited to some properties holds. The controller hands the service the caller's scope from <see cref="IHostScopeResolver"/>, and
/// a caller with no scope (a deactivated member, a member of another org) is refused and gets nothing.
/// </summary>
public class GdprControllerOrgExportTests
{
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private readonly Mock<IGdprService> _gdpr = new();
    private readonly Mock<IOrgContextResolver> _org = new();
    private readonly Mock<IHostScopeResolver> _scopes = new();
    private readonly GdprController _controller;

    public GdprControllerOrgExportTests()
    {
        _controller = new GdprController(
            _gdpr.Object,
            _org.Object,
            Mock.Of<IGuestAccessService>(),
            Mock.Of<IAuthorizationService>(),
            _scopes.Object,
            Mock.Of<ILogger<GdprController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|owner"), new Claim(ClaimTypes.Role, "PropertyOwner")], "Test")),
                },
            },
        };
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
    }

    private void CallerReaches(HostScope? scope) =>
        _scopes
            .Setup(r => r.ResolveAsync("auth0|owner", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);

    [Fact]
    public void ExportOrgFiscal_NeedsTheOrgPolicy_NotThePropertyOne()
    {
        var method = typeof(GdprController).GetMethod(nameof(GdprController.ExportOrgFiscal))!;

        var policies = method.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).ToList();

        Assert.Equal([CasazenPolicies.OrgBillingAdmin], policies);
    }

    [Fact]
    public async Task ExportOrgFiscal_HandsTheServiceTheScopeOfTheCaller()
    {
        var scope = new HostScope(OrgId);
        CallerReaches(scope);
        var data = new Dictionary<string, object> { ["fiscalCode"] = "RSSMRA80A01H501U" };
        _gdpr.Setup(g => g.ExportOrgFiscalDataAsync(OrgId, scope, It.IsAny<CancellationToken>())).ReturnsAsync(data);

        var result = await _controller.ExportOrgFiscal(CancellationToken.None);

        Assert.Same(data, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task ExportOrgFiscal_ANarrowerScope_IsPassedAsItIs_NeverWidened()
    {
        var scope = new HostScope(OrgId, OwnerId: "auth0|owner");
        CallerReaches(scope);
        _gdpr
            .Setup(g => g.ExportOrgFiscalDataAsync(OrgId, scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, object>());

        await _controller.ExportOrgFiscal(CancellationToken.None);

        _gdpr.Verify(g => g.ExportOrgFiscalDataAsync(OrgId, scope, It.IsAny<CancellationToken>()), Times.Once);
        _gdpr.Verify(g => g.ExportOrgFiscalDataAsync(OrgId, It.Is<HostScope>(s => s.IsOrgWide), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExportOrgFiscal_ACallerWithNoScope_IsForbidden_AndExportsNothing()
    {
        CallerReaches(null);

        var result = await _controller.ExportOrgFiscal(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        _gdpr.Verify(
            g => g.ExportOrgFiscalDataAsync(It.IsAny<Guid>(), It.IsAny<HostScope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExportOrgFiscal_NoOrg_IsNotFound_AndExportsNothing()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var result = await _controller.ExportOrgFiscal(CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        _gdpr.Verify(
            g => g.ExportOrgFiscalDataAsync(It.IsAny<Guid>(), It.IsAny<HostScope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
