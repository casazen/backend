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

public class AdminConcordatoFacileControllerTests
{
    private const string AdminUserId = "auth0|admin";

    [Fact]
    public void GrantGift_NonAdminUser_Returns403()
    {
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

        var gift = await db.ConcordatoFacileGifts.FirstOrDefaultAsync(g => g.OrgId == orgId);
        Assert.NotNull(gift);
        Assert.Equal(request.SubscriptionId, gift.GrantedBySubscriptionId);
        Assert.Equal(ConcordatoFacileGiftStatus.Available, gift.Status);

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

        if (orgId.HasValue)
        {
            db.Orgs.Add(new Core.Entities.Org { Id = orgId.Value, Name = "Test Org", Slug = "test-org" });
        }

        if (existingGift != null)
        {
            db.ConcordatoFacileGifts.Add(existingGift);
        }

        await db.SaveChangesAsync();

        var activityLog = new Mock<IActivityLog>();
        activityLog.Setup(a => a.Record(It.IsAny<OrgActivity>()))
            .Callback<OrgActivity>(activity =>
            {
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

        var serviceLogger = new Mock<ILogger<Casazen.Infrastructure.Services.ConcordatoFacileAdminService>>();
        var concordatoFacileService = new Casazen.Infrastructure.Services.ConcordatoFacileAdminService(
            db, activityLog.Object, serviceLogger.Object);

        var logger = new Mock<ILogger<AdminConcordatoFacileController>>();

        var controller = new AdminConcordatoFacileController(db, concordatoFacileService, logger.Object)
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
