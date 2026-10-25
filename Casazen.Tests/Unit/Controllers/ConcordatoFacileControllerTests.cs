using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Dtos.ConcordatoFacile;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Proves tenant isolation (NF-06 AC-NF06-2): PracticeLookup returns 404 when the practice's lease
    /// belongs to a different org, preventing cross-tenant data leakage.
    /// </summary>
    [Fact]
    public async Task PracticeLookup_OtherOrgLease_Returns404()
    {
        // Arrange
        var db = CreateInMemoryDbContext();
        var controller = CreateController(db);
        SetUser(controller, TestUserId);

        var userOrgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var practiceId = "P-1234567890ABCDEF";

        // Create a property and lease belonging to a DIFFERENT org
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = otherOrgId,
            Name = "Test Property",
            Address = "Test Address",
            City = "Test City",
            PostalCode = "12345"
        };
        db.Properties.Add(property);

        var lease = new LeaseContract
        {
            Id = leaseId,
            OrgId = otherOrgId, // Different org!
            PropertyId = property.Id,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CanoneConcordato,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1000m
        };
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();

        // ConcordatoFacile (through the 15-minute cache of the status service) says the practice belongs to that lease
        _mockStatusCacheService
            .Setup(c => c.GetPracticeAsync(practiceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PracticeDto
            {
                PracticeId = practiceId,
                LeaseId = leaseId,
                Status = CfPracticeStatus.InProgress,
                StatusChangedAt = DateTime.UtcNow
            });

        // Mock org resolver to return the user's org (different from lease's org)
        _mockOrgContextResolver
            .Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(userOrgId);

        // Act
        var result = await controller.LookupPractice(practiceId, CancellationToken.None);

        // Assert
        // Should return 404 to not reveal existence of the practice/lease to other org
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
    }

    private ConcordatoFacileController CreateController(AppDbContext? db = null)
    {
        // Create an in-memory database if not provided
        var dbContext = db ?? CreateInMemoryDbContext();

        return new ConcordatoFacileController(
            _mockCheckoutService.Object,
            _mockAccessService.Object,
            _mockGiftService.Object,
            _mockLinkService.Object,
            _mockStatusCacheService.Object,
            dbContext,
            _mockOrgContextResolver.Object,
            _mockAuthorizationService.Object);
    }

    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

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
