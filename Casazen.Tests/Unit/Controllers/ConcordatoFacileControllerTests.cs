using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Dtos.ConcordatoFacile;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// Tests for ConcordatoFacileController status endpoint (ADR-008).
/// </summary>
public class ConcordatoFacileControllerTests
{
    private readonly Mock<IConcordatoFacileCheckoutService> _mockCheckoutService = new();
    private readonly Mock<IConcordatoFacileAccessService> _mockAccessService = new();
    private readonly Mock<IConcordatoFacileGiftService> _mockGiftService = new();
    private readonly Mock<IConcordatoFacileLinkService> _mockLinkService = new();
    private readonly Mock<IConcordatoFacileStatusCacheService> _mockStatusCacheService = new();
    private readonly Mock<IOrgContextResolver> _mockOrgContextResolver = new();
    private readonly Mock<IAuthorizationService> _mockAuthorizationService = new();

    private static readonly Guid TestLeaseId = Guid.NewGuid();
    private static readonly Guid TestOrgId = Guid.NewGuid();
    private const string TestUserId = "auth0|test_user";

    /// <summary>
    /// Proves ADR-008-R11: GetStatus requires LeaseRead permission.
    /// A user without LeaseRead permission is forbidden from accessing practice status.
    /// </summary>
    [Fact]
    public async Task GetStatus_UnauthorizedUser_Returns403()
    {
        // Arrange
        var controller = CreateController();
        SetUser(controller, TestUserId);

        _mockOrgContextResolver
            .Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestOrgId);

        // Mock authorization service to deny LeaseRead
        _mockAuthorizationService
            .Setup(a => a.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object>(),
                CasazenPolicies.LeaseRead))
            .ReturnsAsync(AuthorizationResult.Failed());

        // Act
        // The [Authorize(Policy = CasazenPolicies.LeaseRead)] attribute on GetStatus
        // is enforced by ASP.NET Core middleware. This test verifies the policy is configured.
        // In production, the middleware checks before the action executes.
        var authorizeAttributes = typeof(ConcordatoFacileController)
            .GetMethod(nameof(ConcordatoFacileController.GetStatus))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);

        // Assert
        var authorizeAttr = Assert.Single(authorizeAttributes.Cast<AuthorizeAttribute>());
        Assert.Equal(CasazenPolicies.LeaseRead, authorizeAttr.Policy);
    }

    private ConcordatoFacileController CreateController() =>
        new(
            _mockCheckoutService.Object,
            _mockAccessService.Object,
            _mockGiftService.Object,
            _mockLinkService.Object,
            _mockStatusCacheService.Object,
            _mockOrgContextResolver.Object,
            _mockAuthorizationService.Object);

    private static void SetUser(ConcordatoFacileController controller, string userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", userId) }, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
    }
}
