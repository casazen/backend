using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// AM-02b: the endpoints of the activity log and of the requests for access. The org and the account are the caller's own (never
/// from the request), a caller with no org gets 404, the filters of the query reach the service as the caller wrote them (and a
/// wrong one is a 400 that says which), the language of the emails is the one of the request; and the wiring that the
/// architecture needs: the flag, the policy, the rate limit.
/// </summary>
public class OrgActivityControllersTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly DateTime Now = new(2026, 10, 9, 10, 30, 0, DateTimeKind.Utc);

    private readonly Mock<IOrgActivityService> _activity = new();
    private readonly Mock<IOrgAccessRequestService> _requests = new();
    private readonly Mock<IOrgContextResolver> _org = new();

    public OrgActivityControllersTests()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
    }

    private static T WithContext<T>(T controller, params Claim[] claims)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        return controller;
    }

    private OrgActivityController Activity(string? sub = "auth0|owner") => WithContext(
        new OrgActivityController(_activity.Object, _org.Object, new Casazen.Tests.Unit.FakeTimeProvider(new DateTimeOffset(Now))),
        sub is null ? [] : [new Claim("sub", sub)]);

    private OrgAccessRequestsController Requests(string? sub = "auth0|collab") => WithContext(
        new OrgAccessRequestsController(_requests.Object, _org.Object),
        sub is null ? [] : [new Claim("sub", sub)]);

    private static OrgActivityItem Item(OrgActivityType type = OrgActivityType.MemberRoleChanged) => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Now,
        "auth0|owner",
        OrgActivityArea.ShortRent,
        type,
        OrgActivitySubjectType.Member,
        "auth0|anna",
        new Dictionary<string, string> { ["fromRole"] = "Collaborator", ["toRole"] = "Admin" });

    private static async Task<ObjectResult> ProblemAsync(Task<ActionResult<PagedResultDto<OrgActivityEntryDto>>> call)
    {
        var result = await call;
        return Assert.IsAssignableFrom<ObjectResult>(result.Result);
    }

    // ─── The list ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_AsksTheServiceForTheCallersOrgOnly_AndShapesTheAnswerLikeTheOtherPages()
    {
        OrgActivityFilter? filter = null;
        _activity.Setup(a => a.ListAsync(OrgId, It.IsAny<OrgActivityFilter>(), 2, 20, It.IsAny<CancellationToken>()))
            .Callback<Guid, OrgActivityFilter, int, int, CancellationToken>((_, f, _, _, _) => filter = f)
            .ReturnsAsync(new OrgActivityPage([Item()], 41, 2, 20));

        var result = await Activity().List(new OrgActivityListQuery { Page = 2, PageSize = 20 }, CancellationToken.None);

        var page = Assert.IsType<PagedResultDto<OrgActivityEntryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((41, 2, 20), (page.TotalCount, page.Page, page.PageSize));
        var entry = Assert.Single(page.Items);
        Assert.Equal(
            ("short-rent", OrgActivityType.MemberRoleChanged, OrgActivitySubjectType.Member, "auth0|owner", "auth0|anna"),
            (entry.Area, entry.Type, entry.SubjectType, entry.ActorUserId, entry.SubjectId));
        Assert.Equal(new Dictionary<string, string> { ["fromRole"] = "Collaborator", ["toRole"] = "Admin" }, entry.Details);
        Assert.Equal(new OrgActivityFilter(), filter);
    }

    [Fact]
    public async Task List_TheFiltersReachTheServiceAsWritten()
    {
        OrgActivityFilter? filter = null;
        _activity.Setup(a => a.ListAsync(OrgId, It.IsAny<OrgActivityFilter>(), 1, 50, It.IsAny<CancellationToken>()))
            .Callback<Guid, OrgActivityFilter, int, int, CancellationToken>((_, f, _, _, _) => filter = f)
            .ReturnsAsync(new OrgActivityPage([], 0, 1, 50));

        await Activity().List(
            new OrgActivityListQuery
            {
                From = Now.AddDays(-7),
                To = Now,
                Type = ["MemberInvited", "memberroleChanged, PlanChanged"],
                Area = "Short-Rent",
                Actor = " auth0|admin ",
            },
            CancellationToken.None);

        Assert.NotNull(filter);
        Assert.Equal((Now.AddDays(-7), Now), (filter.From, filter.To));
        Assert.Equal(
            [OrgActivityType.MemberInvited, OrgActivityType.MemberRoleChanged, OrgActivityType.PlanChanged],
            filter.Types);
        Assert.Equal((OrgActivityArea.ShortRent, "auth0|admin", false), (filter.Area, filter.ActorUserId, filter.SystemActor));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("SYSTEM")]
    [InlineData(" System ")]
    public async Task List_TheActorSystem_MeansNoPerson(string actor)
    {
        OrgActivityFilter? filter = null;
        _activity.Setup(a => a.ListAsync(OrgId, It.IsAny<OrgActivityFilter>(), 1, 50, It.IsAny<CancellationToken>()))
            .Callback<Guid, OrgActivityFilter, int, int, CancellationToken>((_, f, _, _, _) => filter = f)
            .ReturnsAsync(new OrgActivityPage([], 0, 1, 50));

        await Activity().List(new OrgActivityListQuery { Actor = actor }, CancellationToken.None);

        Assert.Equal((null, true), (filter!.ActorUserId, filter.SystemActor));
    }

    [Theory]
    [InlineData("NotAnEvent")]
    [InlineData("MemberInvited,Nope")]
    [InlineData("7")]
    public async Task List_AnUnknownEvent_Is400WithTheValidationCode(string type)
    {
        var problem = await ProblemAsync(Activity().List(new OrgActivityListQuery { Type = [type] }, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);
        Assert.Equal(ProblemCodes.ValidationError, details.Extensions[ApiProblemDetails.CodeExtension]);
        _activity.Verify(a => a.ListAsync(It.IsAny<Guid>(), It.IsAny<OrgActivityFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("ShortRent")]
    public async Task List_AnUnknownArea_Is400(string area)
    {
        var problem = await ProblemAsync(Activity().List(new OrgActivityListQuery { Area = area }, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task List_AStartAfterTheEnd_Is400()
    {
        var problem = await ProblemAsync(Activity().List(new OrgActivityListQuery { From = Now, To = Now.AddDays(-1) }, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task List_ACallerWithNoOrg_Is404_AndWithNoAccount_Is401()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var noOrg = await Activity().List(new OrgActivityListQuery(), CancellationToken.None);
        var noAccount = await Activity(sub: null).List(new OrgActivityListQuery(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(noOrg.Result).StatusCode);
        Assert.IsType<UnauthorizedResult>(noAccount.Result);
    }

    // ─── The CSV ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_IsTheCsvOfTheCallersOrg_NamedByTheDay()
    {
        OrgActivityFilter? filter = null;
        _activity.Setup(a => a.StreamAsync(OrgId, It.IsAny<OrgActivityFilter>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, OrgActivityFilter, CancellationToken>((_, f, _) => filter = f)
            .Returns(One(Item()));

        var result = await Activity().Export(new OrgActivityListQuery { Type = ["MemberRoleChanged"] }, CancellationToken.None);

        Assert.IsType<OrgActivityCsvResult>(result);
        Assert.Equal([OrgActivityType.MemberRoleChanged], filter!.Types);

        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await ((OrgActivityCsvResult)result).ExecuteResultAsync(new ActionContext(context, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        Assert.Contains("activity-20261009.csv", context.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_AWrongFilter_Is400_AndANoOrgCaller_Is404()
    {
        var wrong = await Activity().Export(new OrgActivityListQuery { Area = "nope" }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ObjectResult>(wrong).StatusCode);

        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var noOrg = await Activity().Export(new OrgActivityListQuery(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(noOrg).StatusCode);
    }

    private static async IAsyncEnumerable<OrgActivityItem> One(OrgActivityItem item)
    {
        yield return item;
        await Task.CompletedTask;
    }

    // ─── The request for access ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PassesTheCallersOrgAndAccount_WithTheNoteAndTheLanguageOfTheRequest_Answers202()
    {
        RequestOrgAccess? received = null;
        _requests.Setup(r => r.RequestAsync(It.IsAny<RequestOrgAccess>(), It.IsAny<CancellationToken>()))
            .Callback<RequestOrgAccess, CancellationToken>((r, _) => received = r)
            .ReturnsAsync(new OrgAccessRequested(2));
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en");
        try
        {
            var result = await Requests().Create(new RequestOrgAccessRequest { Area = "billing", Note = "Please" }, CancellationToken.None);

            var accepted = Assert.IsType<AcceptedResult>(result.Result);
            Assert.Equal(2, Assert.IsType<OrgAccessRequestedDto>(accepted.Value).Notified);
            Assert.Equal(new RequestOrgAccess(OrgId, "auth0|collab", "billing", "Please", "en"), received);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task Create_ACallerWithNoOrg_Is404_AndWithNoAccount_Is401_AndTheServiceIsNotCalled()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var noOrg = await Requests().Create(new RequestOrgAccessRequest { Area = "billing" }, CancellationToken.None);
        var noAccount = await Requests(sub: null).Create(new RequestOrgAccessRequest { Area = "billing" }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(noOrg.Result).StatusCode);
        Assert.IsType<UnauthorizedResult>(noAccount.Result);
        _requests.Verify(r => r.RequestAsync(It.IsAny<RequestOrgAccess>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── The wiring ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BothControllers_AreBehindTheOrgTeamFlag()
    {
        foreach (var controller in new[] { typeof(OrgActivityController), typeof(OrgAccessRequestsController) })
        {
            var gate = controller.GetCustomAttribute<FeatureGateAttribute>();
            Assert.NotNull(gate);
            Assert.Equal(FeatureFlags.OrgTeam, gate.Flag);
        }
    }

    [Fact]
    public void TheActivityLog_NeedsTheActivityReadPermission_OfTheAccountContext()
    {
        var policy = typeof(OrgActivityController).GetCustomAttribute<AuthorizeAttribute>()!.Policy;

        Assert.Equal(CasazenPolicies.OrgActivityRead, policy);
        Assert.Equal("RequireContext:account:org.activity.read", policy);
        Assert.Contains(CasazenPolicies.OrgActivityRead, CasazenPolicies.ContextPolicies);
    }

    [Fact]
    public void TheRequestForAccess_IsForAnySignedInMember_AndRateLimitedPerPerson()
    {
        var create = typeof(OrgAccessRequestsController).GetMethod(nameof(OrgAccessRequestsController.Create))!;

        Assert.Equal(CasazenPolicies.Authenticated, create.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(RateLimitPolicies.OrgAccessRequest, create.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
    }
}
