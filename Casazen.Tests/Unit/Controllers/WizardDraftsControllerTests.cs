using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Wizards;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// <c>api/wizards/{type}/draft</c> (ADR-024, E01/F02/US01): the person is always the token's subject and the org the caller's own, the draft
/// of somebody else is a 404, a client behind the server gets the 409 <c>draft_conflict</c> with the server's draft as members of the
/// problem, and what comes back is private. The rules of the draft itself are in <c>WizardDraftServiceTests</c>.
/// </summary>
public class WizardDraftsControllerTests
{
    private const string UserId = "auth0|owner";

    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid BookingId = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly DateTime SavedAt = new(2026, 11, 5, 9, 30, 15, DateTimeKind.Utc);

    private readonly Mock<IWizardDraftService> _drafts = new();
    private readonly Mock<IOrgContextResolver> _orgs = new();

    private WizardDraftsController NewController(string? sub = UserId, bool hasOrg = true)
    {
        _orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(hasOrg ? (Guid?)OrgId : null);

        var identity = sub is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim("sub", sub)], "TestAuth");
        return new WizardDraftsController(_drafts.Object, _orgs.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static WizardDraftDto Draft(WizardType type, Guid? entityId, int version, string json) =>
        new(type, entityId, version, JsonDocument.Parse(json), SavedAt);

    [Fact]
    public void TheEndpoint_IsTheOneTheClientsCall()
    {
        var route = typeof(WizardDraftsController).GetCustomAttribute<RouteAttribute>();

        Assert.Equal("api/wizards/{type}/draft", route!.Template);
        Assert.NotNull(typeof(WizardDraftsController).GetMethod(nameof(WizardDraftsController.Get))!.GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(typeof(WizardDraftsController).GetMethod(nameof(WizardDraftsController.Save))!.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(typeof(WizardDraftsController).GetMethod(nameof(WizardDraftsController.Delete))!.GetCustomAttribute<HttpDeleteAttribute>());
    }

    [Fact]
    public async Task Post_AClientBehindTheServer_Is409DraftConflict_WithTheServerDraftAndItsVersion()
    {
        _drafts
            .Setup(d => d.SaveDraftAsync(OrgId, UserId, It.IsAny<SaveDraftRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WizardDraftResult(DraftStatus.Conflict, 2, Draft(WizardType.ListingCreate, null, 2, """{"name":"Casa due"}""")));
        var controller = NewController();
        using var mine = JsonDocument.Parse("""{"name":"Casa tre"}""");

        var result = await controller.Save(
            "ListingCreate", new SaveWizardDraftRequest { ClientVersion = 1, Data = mine }, CancellationToken.None);

        var problem = AssertProblem(result.Result, StatusCodes.Status409Conflict);
        Assert.Equal("draft_conflict", problem.Extensions["code"]);
        Assert.Equal(2, problem.Extensions["serverVersion"]);
        var serverDraft = Assert.IsType<WizardDraftResponse>(problem.Extensions["serverDraft"]);
        Assert.Equal((WizardType.ListingCreate, 2, SavedAt), (serverDraft.Type, serverDraft.Version, serverDraft.LastSavedAt));
        Assert.Equal("Casa due", serverDraft.Data.GetProperty("name").GetString());
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());

        // The person is the token's subject and the org the caller's own; the wizard is the one of the address.
        _drafts.Verify(
            d => d.SaveDraftAsync(
                OrgId,
                UserId,
                It.Is<SaveDraftRequest>(r => r.Type == WizardType.ListingCreate && r.ClientVersion == 1 && r.EntityId == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Post_ASavedDraft_Is200_WithTheVersionAndTheInstantOfTheSave()
    {
        _drafts
            .Setup(d => d.SaveDraftAsync(OrgId, UserId, It.IsAny<SaveDraftRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WizardDraftResult(DraftStatus.Saved, 1, Draft(WizardType.CheckIn, BookingId, 1, """{"step":2}""")));
        var controller = NewController();
        using var data = JsonDocument.Parse("""{"step":2}""");

        var result = await controller.Save(
            "checkin",
            new SaveWizardDraftRequest { Type = WizardType.CheckIn, EntityId = BookingId, ClientVersion = 0, Data = data },
            CancellationToken.None);

        var body = Assert.IsType<WizardDraftResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((WizardType.CheckIn, (Guid?)BookingId, 1, SavedAt), (body.Type, body.EntityId, body.Version, body.LastSavedAt));
        Assert.Equal(2, body.Data.GetProperty("step").GetInt32());
        _drafts.Verify(
            d => d.SaveDraftAsync(
                OrgId,
                UserId,
                It.Is<SaveDraftRequest>(r => r.Type == WizardType.CheckIn && r.EntityId == BookingId && r.ClientVersion == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Post_ATypeInTheBodyThatIsNotTheOneOfTheAddress_OrDataThatIsNotAnObject_IsRefused400_AndNothingIsSaved()
    {
        var controller = NewController();
        using var data = JsonDocument.Parse("""{"step":2}""");
        using var list = JsonDocument.Parse("[1]");

        var mismatch = await controller.Save(
            "CheckIn", new SaveWizardDraftRequest { Type = WizardType.CheckOut, Data = data }, CancellationToken.None);
        var notAnObject = await controller.Save("CheckIn", new SaveWizardDraftRequest { Data = list }, CancellationToken.None);
        var noData = await controller.Save("CheckIn", new SaveWizardDraftRequest { Data = null }, CancellationToken.None);

        Assert.Equal("wizard_type_mismatch", AssertProblem(mismatch.Result, StatusCodes.Status400BadRequest).Extensions["code"]);
        Assert.Equal("wizard_draft_data_invalid", AssertProblem(notAnObject.Result, StatusCodes.Status400BadRequest).Extensions["code"]);
        Assert.Equal("wizard_draft_data_invalid", AssertProblem(noData.Result, StatusCodes.Status400BadRequest).Extensions["code"]);
        _drafts.Verify(
            d => d.SaveDraftAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<SaveDraftRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Get_TheCallersDraft_Is200_PrivateAndNotCached()
    {
        _drafts
            .Setup(d => d.GetDraftAsync(OrgId, UserId, WizardType.BookingCreate, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Draft(WizardType.BookingCreate, null, 3, """{"guestName":"Giulia Verdi","step":2}"""));
        var controller = NewController();

        var result = await controller.Get("BookingCreate", null, CancellationToken.None);

        var body = Assert.IsType<WizardDraftResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(3, body.Version);
        Assert.Equal("Giulia Verdi", body.Data.GetProperty("guestName").GetString());
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Get_NoDraftOfTheCaller_Is404_AndTheDraftLookedForIsThatOfTheTokensSubject()
    {
        // The draft of another person is the same case for the service: it answers none, and the caller is told what it is told for no draft.
        _drafts
            .Setup(d => d.GetDraftAsync(OrgId, UserId, WizardType.CheckIn, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WizardDraftDto?)null);
        var controller = NewController();

        var result = await controller.Get("checkin", BookingId, CancellationToken.None);

        var problem = AssertProblem(result.Result, StatusCodes.Status404NotFound);
        Assert.Equal("wizard_draft_not_found", problem.Extensions["code"]);
        _drafts.Verify(d => d.GetDraftAsync(OrgId, UserId, WizardType.CheckIn, BookingId, It.IsAny<CancellationToken>()), Times.Once);
        _drafts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Delete_DiscardsTheDraftOfTheTokensSubject_And204()
    {
        var controller = NewController();

        var result = await controller.Delete("CheckIn", BookingId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _drafts.Verify(d => d.DeleteDraftAsync(OrgId, UserId, WizardType.CheckIn, BookingId, It.IsAny<CancellationToken>()), Times.Once);
        _drafts.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("99")]
    [InlineData("3")] // the number of a member is not its name
    public async Task EveryAction_AnUnknownWizardType_Is400_AndNothingIsTouched(string type)
    {
        var controller = NewController();
        using var data = JsonDocument.Parse("{}");

        var get = await controller.Get(type, null, CancellationToken.None);
        var save = await controller.Save(type, new SaveWizardDraftRequest { Data = data }, CancellationToken.None);
        var delete = await controller.Delete(type, null, CancellationToken.None);

        Assert.Equal("wizard_type_unknown", AssertProblem(get.Result, StatusCodes.Status400BadRequest).Extensions["code"]);
        Assert.Equal("wizard_type_unknown", AssertProblem(save.Result, StatusCodes.Status400BadRequest).Extensions["code"]);
        Assert.Equal("wizard_type_unknown", AssertProblem(delete, StatusCodes.Status400BadRequest).Extensions["code"]);
        _drafts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EveryAction_WithoutASubject_IsUnauthorized_AndWithoutAnOrg_Is404_AndNothingIsTouched()
    {
        using var data = JsonDocument.Parse("{}");
        var anonymous = NewController(sub: null);

        Assert.IsType<UnauthorizedResult>((await anonymous.Get("CheckIn", null, CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await anonymous.Save("CheckIn", new SaveWizardDraftRequest { Data = data }, CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>(await anonymous.Delete("CheckIn", null, CancellationToken.None));

        // A caller with no host org (a supplier-only account) has no drafts.
        var noOrg = NewController(hasOrg: false);
        Assert.Equal(
            "not_found",
            AssertProblem((await noOrg.Get("CheckIn", null, CancellationToken.None)).Result, StatusCodes.Status404NotFound).Extensions["code"]);
        AssertProblem((await noOrg.Save("CheckIn", new SaveWizardDraftRequest { Data = data }, CancellationToken.None)).Result, StatusCodes.Status404NotFound);
        AssertProblem(await noOrg.Delete("CheckIn", null, CancellationToken.None), StatusCodes.Status404NotFound);

        _drafts.VerifyNoOtherCalls();
    }

    private static ProblemDetails AssertProblem(IActionResult? result, int status)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }
}
