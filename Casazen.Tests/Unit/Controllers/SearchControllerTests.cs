using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Search;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Search;
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
/// UI-13a: <c>GET /api/search</c> as the controller handles it: the term is validated before anything is read, the groups the
/// caller may read come from the access resolver, the answer is mapped without a field that was not meant to leave, it is never
/// cached; and the endpoint is behind the flag, rate limited per user and listed as authenticated-only with its reason.
/// </summary>
public class SearchControllerTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    private readonly Mock<ISearchAccessResolver> _access = new();
    private readonly Mock<IGlobalSearchService> _search = new();
    private GlobalSearchRequest? _request;

    private SearchController NewController(SearchAccess? access = null, GlobalSearchResult? result = null)
    {
        access ??= new SearchAccess(new HostSearchAccess(new HostScope(OrgId), true, true, true, true, true), null);
        _access.Setup(a => a.ResolveAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>())).ReturnsAsync(access);
        _search
            .Setup(s => s.SearchAsync(It.IsAny<GlobalSearchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<GlobalSearchRequest, CancellationToken>((request, _) => _request = request)
            .ReturnsAsync(result ?? GlobalSearchResult.Empty);

        return new SearchController(_access.Object, _search.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|someone")], "Test")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static ProblemDetails AssertProblem(IActionResult? result, string code)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(code, problem.Extensions["code"]);
        return problem;
    }

    // ─── The term ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData("  a  ")]
    public async Task Search_ATermShorterThanTwoCharacters_Is400_BeforeAnythingIsRead(string? q)
    {
        var controller = NewController();

        var result = await controller.Search(q, null, CancellationToken.None);

        var problem = AssertProblem(result.Result, SearchErrorCodes.QueryTooShort);
        Assert.Contains("2", problem.Detail);
        Assert.Equal("search_query_too_short", SearchErrorCodes.QueryTooShort);
        _access.VerifyNoOtherCalls();
        _search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Search_ATermLongerThanTheMaximum_Is400()
    {
        var controller = NewController();

        var result = await controller.Search(new string('a', SearchText.MaxQueryLength + 1), null, CancellationToken.None);

        var problem = AssertProblem(result.Result, SearchErrorCodes.QueryTooLong);
        Assert.Contains(SearchText.MaxQueryLength.ToString(CultureInfo.InvariantCulture), problem.Detail);
        Assert.Equal("search_query_too_long", SearchErrorCodes.QueryTooLong);
        _access.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Search_TheMaximumLength_IsAccepted_SpacesAroundTheTermDoNotCount()
    {
        var controller = NewController();

        var padded = "  " + new string('a', SearchText.MaxQueryLength) + "  ";
        var result = await controller.Search(padded, null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_ATermWithNothingToSearch_IsAnEmptyAnswer_NotAnError()
    {
        var controller = NewController();

        // Two characters, but no letter or digit: it cannot match anything, so nothing is read.
        var result = await controller.Search("!?", null, CancellationToken.None);

        var response = Assert.IsType<GlobalSearchResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(response.Groups);
        _access.VerifyNoOtherCalls();
        _search.VerifyNoOtherCalls();
    }

    // ─── The answer ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_RunsTheTermWithTheAccessOfTheCaller_AndTheLimitOfTheDefault()
    {
        var access = new SearchAccess(
            new HostSearchAccess(new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"), true, false, true, false, false),
            Guid.Parse("0b6d6a3e-3d1f-4f0a-9d7e-5b0c4f2a1e22"));
        var controller = NewController(access);

        await controller.Search("  Rossi  MARIA ", null, CancellationToken.None);

        Assert.NotNull(_request);
        Assert.Equal(["rossi", "maria"], _request!.Query.Tokens);
        Assert.Equal(SearchLimits.DefaultLimit, _request.Limit);
        Assert.Equal(access.Host, _request.Host);
        Assert.Equal(access.SupplierOrgId, _request.SupplierOrgId);
        Assert.Equal(CultureInfo.CurrentUICulture, _request.Culture);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    [InlineData(100000, 20)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    public async Task Search_TheLimitIsPerGroup_AtMostTwenty_AtLeastOne(int? limit, int expected)
    {
        var controller = NewController();

        await controller.Search("rossi", limit, CancellationToken.None);

        Assert.Equal(expected, _request!.Limit);
        Assert.Equal(20, SearchLimits.MaxLimit);
        Assert.Equal(5, SearchLimits.DefaultLimit);
    }

    [Fact]
    public async Task Search_ACallerWhoMaySearchNothing_GetsAnEmptyAnswer_AndTheServiceIsNotCalled()
    {
        var controller = NewController(new SearchAccess(null, null));

        var result = await controller.Search("rossi", null, CancellationToken.None);

        Assert.Empty(Assert.IsType<GlobalSearchResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Groups);
        _search.Verify(s => s.SearchAsync(It.IsAny<GlobalSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_MapsTheGroupsAsTheyCome_WithOnlyTheFieldsOfTheContract()
    {
        var hit = new SearchHit("guest", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Maria Rossi", "m***@example.com", "short-rent.guest");
        var controller = NewController(result: new GlobalSearchResult([new SearchGroup("guest", [hit], HasMore: true)]));

        var result = await controller.Search("rossi", 3, CancellationToken.None);

        var response = Assert.IsType<GlobalSearchResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var group = Assert.Single(response.Groups);
        Assert.Equal(("guest", true), (group.Type, group.HasMore));
        Assert.Equal(new SearchHitDto("guest", hit.Id, "Maria Rossi", "m***@example.com", "short-rent.guest"), Assert.Single(group.Items));
    }

    [Fact]
    public void TheContract_AHitCarriesOnlyTypeIdTitleSubtitleAndDestination()
    {
        Assert.Equal(
            ["Destination", "Id", "Subtitle", "Title", "Type"],
            typeof(SearchHitDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract").Select(p => p.Name).Order().ToArray());
        Assert.Equal(
            ["HasMore", "Items", "Type"],
            typeof(SearchGroupDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract").Select(p => p.Name).Order().ToArray());
        Assert.Equal(
            ["Groups"],
            typeof(GlobalSearchResponse).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "EqualityContract").Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("rossi")]
    public async Task Search_TheAnswerIsNeverCached(string? q)
    {
        var controller = NewController();

        await controller.Search(q ?? "!?", null, CancellationToken.None);

        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    // ─── The endpoint ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEndpoint_IsBehindTheFlag_RateLimitedPerUser_AndRequiresASignedInUser()
    {
        var controller = typeof(SearchController);

        Assert.Equal(FeatureFlags.GlobalSearch, Assert.Single(controller.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.Equal(RateLimitPolicies.GlobalSearch, Assert.Single(controller.GetCustomAttributes<EnableRateLimitingAttribute>()).PolicyName);
        Assert.Equal(CasazenPolicies.Authenticated, Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Equal("api/search", Assert.Single(controller.GetCustomAttributes<RouteAttribute>()).Template);

        var action = controller.GetMethod(nameof(SearchController.Search))!;
        Assert.NotNull(action.GetCustomAttribute<HttpGetAttribute>());
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public void TheEndpoint_TakesOnlyTheTermAndTheLimit_FromTheQuery()
    {
        var parameters = typeof(SearchController).GetMethod(nameof(SearchController.Search))!.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .ToList();

        Assert.Equal(["q", "limit"], parameters.Select(p => p.Name));
        Assert.All(parameters, p => Assert.NotNull(p.GetCustomAttribute<FromQueryAttribute>()));
    }
}
