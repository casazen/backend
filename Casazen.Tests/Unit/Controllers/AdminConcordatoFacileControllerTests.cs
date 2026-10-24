using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.ConcordatoFacile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// Admin ConcordatoFacile gift management controller: verifies AdminOnly authorization and the 409 conflict
/// when an org already has a gift (ADR-006-R9). The controller's only business logic is the existence check;
/// the gift creation and activity log recording are straightforward database operations.
/// </summary>
public class AdminConcordatoFacileControllerTests
{
    private const string AdminUserId = "auth0|admin";

    /// <summary>
    /// Proves the permission rule (ADR-006-R9): the grant-gift endpoint requires AdminOnly policy,
    /// ensuring non-admin users receive 403 Forbidden from the authorization middleware.
    /// </summary>
    [Fact]
    public void GrantGift_NonAdminUser_Returns403()
    {
        // The [Authorize(Policy = CasazenPolicies.AdminOnly)] attribute on the controller ensures
        // that non-admin users are blocked by the authorization middleware with 403 before reaching
        // the controller action.
        var controllerType = typeof(AdminConcordatoFacileController);
        var authorizeAttributes = controllerType
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .ToList();

        Assert.NotEmpty(authorizeAttributes);
        Assert.Contains(authorizeAttributes, a => a.Policy == CasazenPolicies.AdminOnly);
    }

    [Fact]
    public async Task GrantGift_OrgAlreadyHasGift_Returns409()
    {
        var orgId = Guid.NewGuid();
        var existingGift = new ConcordatoFacileGift
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            GrantedBySubscriptionId = "sub_existing",
            Status = ConcordatoFacileGiftStatus.Available
        };

        var (db, controller) = await CreateControllerAsync(orgId, existingGift);

        var request = new AdminGrantGiftRequest
        {
            SubscriptionId = "sub_new",
            Reason = "Manual grant for existing biennial subscription"
        };

        var result = await controller.GrantGift(orgId, request);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
    }

    [Fact]
    public async Task GrantGift_ValidRequest_CreatesGiftAndRecordsActivity()
    {
        var orgId = Guid.NewGuid();
        var (db, controller) = await CreateControllerAsync(orgId, null);

        var request = new AdminGrantGiftRequest
        {
            SubscriptionId = "sub_12345",
            Reason = "Manual grant for existing biennial subscription"
        };

        var result = await controller.GrantGift(orgId, request);

        var created = Assert.IsType<CreatedResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);

        // Verify gift was created
        var gift = await db.ConcordatoFacileGifts.FirstOrDefaultAsync(g => g.OrgId == orgId);
        Assert.NotNull(gift);
        Assert.Equal(request.SubscriptionId, gift.GrantedBySubscriptionId);
        Assert.Equal(ConcordatoFacileGiftStatus.Available, gift.Status);

        // Verify activity log entry was created
        var activityEntry = await db.OrgActivityEntries
            .FirstOrDefaultAsync(e => e.OrgId == orgId && e.Type == OrgActivityType.AdminGrantedConcordatoFacileGift);
        Assert.NotNull(activityEntry);
        Assert.Equal(AdminUserId, activityEntry.ActorUserId);
    }

    [Fact]
    public async Task GrantGift_OrgDoesNotExist_Returns404()
    {
        var nonExistentOrgId = Guid.NewGuid();
        var (db, controller) = await CreateControllerAsync(null, null);

        var request = new AdminGrantGiftRequest
        {
            SubscriptionId = "sub_12345",
            Reason = "Test"
        };

        var result = await controller.GrantGift(nonExistentOrgId, request);

        Assert.IsType<NotFoundResult>(result);
    }

    private static async Task<(AppDbContext, AdminConcordatoFacileController)> CreateControllerAsync(
        Guid? orgId,
        ConcordatoFacileGift? existingGift)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options, null);

        // Seed org if provided
        if (orgId.HasValue)
        {
            db.Orgs.Add(new Org { Id = orgId.Value, Name = "Test Org", Slug = "test-org" });
        }

        // Seed existing gift if provided
        if (existingGift != null)
        {
            db.ConcordatoFacileGifts.Add(existingGift);
        }

        await db.SaveChangesAsync();

        var activityLog = new Mock<IActivityLog>();
        activityLog.Setup(a => a.Record(It.IsAny<OrgActivity>()))
            .Callback<OrgActivity>(activity =>
            {
                // Simulate what the real ActivityLog does: add to DbContext
                db.OrgActivityEntries.Add(new OrgActivityEntry
                {
                    OrgId = activity.OrgId,
                    Type = activity.Type,
                    ActorUserId = activity.ActorUserId,
                    SubjectType = OrgActivitySubjectType.Org,
                    SubjectId = activity.SubjectId,
                    Area = OrgActivityArea.Account,
                    When = DateTime.UtcNow
                });
            });

        var logger = new Mock<ILogger<AdminConcordatoFacileController>>();

        var controller = new AdminConcordatoFacileController(db, activityLog.Object, logger.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", AdminUserId)], "test")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };

        return (db, controller);
    }
}
