using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// AM-02c: the two read endpoints of the home and the matrix of the account. The org and the account are the caller's own (never
/// from the request); a caller with no org gets 404 and one with no account 401; the roles endpoint is for any member (the service
/// checks), the overview for the owner and the administrators; both behind the <c>OrgTeam</c> flag; what the overview does not
/// show is left out of the JSON, not sent as a null.
/// </summary>
public class OrgRolesAndOverviewControllersTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Mock<IOrgRoleMatrixService> _matrix = new();
    private readonly Mock<IOrgOverviewService> _overview = new();
    private readonly Mock<IContextAuthorizationService> _contexts = new();
    private readonly Mock<IHostScopeResolver> _scopes = new();
    private readonly Mock<IOrgContextResolver> _org = new();

    public OrgRolesAndOverviewControllersTests()
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

    private OrgRolesController Roles(string? sub = "auth0|owner") =>
        WithContext(new OrgRolesController(_matrix.Object, _org.Object), sub is null ? [] : [new Claim("sub", sub)]);

    private OrgOverviewController Overview(string? sub = "auth0|owner") =>
        WithContext(
            new OrgOverviewController(_overview.Object, _contexts.Object, _scopes.Object, _org.Object),
            sub is null ? [] : [new Claim("sub", sub)]);

    private static IReadOnlyList<OrgRoleView> TheFiveRoles() => OrgRoleMatrix.Build(
        new Dictionary<(string ContextKey, string RoleKey), IReadOnlyCollection<string>>
        {
            [("account", "org_owner")] = ["org.members.manage", "org.billing.manage"],
            [("account", "org_manager")] = ["org.suppliers.manage"],
            [("short-rent", "property_owner")] = ["booking.write", "booking.read", "property.read"],
            [("short-rent", "staff")] = ["booking.read", "property.read"],
        },
        OrgRole.Owner);

    // ─── The matrix of the roles ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRoles_AsksForTheCallersOrgAndAccount_AndAnswersTheFiveRoles()
    {
        _matrix.Setup(m => m.GetAsync(OrgId, "auth0|owner", It.IsAny<CancellationToken>())).ReturnsAsync(TheFiveRoles());

        var result = await Roles().Get(CancellationToken.None);

        var response = Assert.IsType<OrgRolesResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(
            [OrgRole.Owner, OrgRole.Admin, OrgRole.PropertyManager, OrgRole.Collaborator, OrgRole.Accountant],
            response.Roles.Select(r => r.Role));
        _matrix.VerifyAll();
    }

    [Fact]
    public async Task GetRoles_TheJson_HasTheShapeTheClientReads()
    {
        _matrix.Setup(m => m.GetAsync(OrgId, "auth0|owner", It.IsAny<CancellationToken>())).ReturnsAsync(TheFiveRoles());

        var result = await Roles().Get(CancellationToken.None);

        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value, Json);
        var roles = json.GetProperty("roles");
        Assert.Equal(5, roles.GetArrayLength());
        var owner = roles[0];
        Assert.Equal("Owner", owner.GetProperty("role").GetString());
        Assert.False(owner.GetProperty("assignable").GetBoolean());
        Assert.False(owner.GetProperty("scopeSupported").GetBoolean());
        // Per context: the role key and the permissions, the keys the contexts of the web app have.
        var account = owner.GetProperty("permissionsByContext").GetProperty("account");
        Assert.Equal("org_owner", account.GetProperty("roleKey").GetString());
        Assert.Equal(["org.billing.manage", "org.members.manage"], account.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        // Twelve capabilities with an id, a group and a level in words.
        var capabilities = owner.GetProperty("capabilities");
        Assert.Equal(12, capabilities.GetArrayLength());
        var booking = capabilities[0];
        Assert.Equal(("booking", "short-rent", "Yes"), (booking.GetProperty("id").GetString(), booking.GetProperty("group").GetString(), booking.GetProperty("level").GetString()));
        Assert.Equal("Read", roles[3].GetProperty("capabilities")[0].GetProperty("level").GetString());
        Assert.True(roles[3].GetProperty("scopeSupported").GetBoolean());
    }

    [Fact]
    public async Task GetRoles_ACallerWithNoOrg_Gets404_AndOneWithNoAccount401()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await Roles().Get(CancellationToken.None)).Result).StatusCode);

        Assert.IsType<UnauthorizedResult>((await Roles(sub: null).Get(CancellationToken.None)).Result);
        _matrix.VerifyNoOtherCalls();
    }

    [Fact]
    public void RolesController_IsBehindTheFlag_AndOnlyAskedToBeSignedIn()
    {
        var controller = typeof(OrgRolesController);

        Assert.NotEmpty(controller.GetCustomAttributes(typeof(FeatureGateAttribute), inherit: true));
        // Any active member may read it; the service checks that the caller is one (the policy of the others would shut them out).
        Assert.Equal(
            CasazenPolicies.Authenticated,
            controller.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy);
        Assert.Equal("api/orgs/me/roles", controller.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Cast<RouteAttribute>().Single().Template);
    }

    // ─── The overview ───────────────────────────────────────────────────────────────────────────────────

    private static OrgOverview FullOverview() => new(
        Standing: "attention",
        OpenAccess: false,
        Seats: new OrgOverviewSeats(3, 10, false),
        Properties: new OrgOverviewProperties(6, 4, 2, 2),
        Invitations: new OrgOverviewInvitations(2, 5),
        AccessRequests: new OrgOverviewAccessRequests(0),
        Suppliers: new OrgOverviewSuppliers(0),
        Alloggiati: new OrgOverviewAlloggiati(3),
        RecentActivity:
        [
            new OrgActivityItem(
                Guid.NewGuid(),
                new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc),
                "auth0|owner",
                OrgActivityArea.Account,
                OrgActivityType.MemberInvited,
                OrgActivitySubjectType.Invitation,
                "invito-1",
                new Dictionary<string, string> { ["role"] = "Collaborator" }),
        ]);

    [Fact]
    public async Task GetOverview_PassesTheOrgTheScopeAndTheSectionsTheCallerMaySee()
    {
        var scope = new HostScope(OrgId);
        _scopes.Setup(s => s.ResolveAsync("auth0|owner", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        _contexts.Setup(c => c.HasPermissionAsync("auth0|owner", "account", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string permission, CancellationToken _) =>
                permission is "org.members.manage" or "org.activity.read");
        IReadOnlySet<string>? given = null;
        _overview.Setup(o => o.GetAsync(OrgId, scope, It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, HostScope, IReadOnlySet<string>, CancellationToken>((_, _, permissions, _) => given = permissions)
            .ReturnsAsync(FullOverview());

        var result = await Overview().Get(CancellationToken.None);

        Assert.IsType<OrgOverviewResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.NotNull(given);
        Assert.Equal(new[] { "org.activity.read", "org.members.manage" }, given.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetOverview_TheJson_LeavesOutWhatTheCallerMayNotSee_AndShowsTheRest()
    {
        _scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostScope(OrgId));
        _contexts.Setup(c => c.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _overview.Setup(o => o.GetAsync(OrgId, It.IsAny<HostScope>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgOverview(null, null, new OrgOverviewSeats(1, 2, false), null, null, null, null, null, null));

        var result = await Overview().Get(CancellationToken.None);

        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value, Json);
        Assert.Equal(["seats"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal((1, 2, false), (json.GetProperty("seats").GetProperty("used").GetInt32(), json.GetProperty("seats").GetProperty("max").GetInt32(), json.GetProperty("seats").GetProperty("unlimited").GetBoolean()));
    }

    [Fact]
    public async Task GetOverview_AFullAnswer_HasEverySectionWithTheNamesTheClientReads()
    {
        _scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostScope(OrgId));
        _contexts.Setup(c => c.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _overview.Setup(o => o.GetAsync(OrgId, It.IsAny<HostScope>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FullOverview());

        var json = JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>((await Overview().Get(CancellationToken.None)).Result).Value, Json);

        Assert.Equal(
            new[] { "standing", "openAccess", "seats", "properties", "invitations", "accessRequests", "suppliers", "alloggiati", "recentActivity" }.Order(),
            json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("attention", json.GetProperty("standing").GetString());
        Assert.Equal(
            (6, 4, 2, 2),
            (json.GetProperty("properties").GetProperty("total").GetInt32(), json.GetProperty("properties").GetProperty("short").GetInt32(),
                json.GetProperty("properties").GetProperty("long").GetInt32(), json.GetProperty("properties").GetProperty("toComplete").GetInt32()));
        Assert.Equal((2, 5), (json.GetProperty("invitations").GetProperty("pending").GetInt32(), json.GetProperty("invitations").GetProperty("oldestPendingDays").GetInt32()));
        Assert.Equal(0, json.GetProperty("accessRequests").GetProperty("pending").GetInt32());
        Assert.Equal(0, json.GetProperty("suppliers").GetProperty("trusted").GetInt32());
        Assert.Equal(3, json.GetProperty("alloggiati").GetProperty("propertiesWithoutCredentials").GetInt32());
        var line = json.GetProperty("recentActivity")[0];
        Assert.Equal(("MemberInvited", "account", "invito-1", "auth0|owner"), (line.GetProperty("type").GetString(), line.GetProperty("area").GetString(), line.GetProperty("subjectId").GetString(), line.GetProperty("actorUserId").GetString()));
    }

    [Fact]
    public async Task GetOverview_ACallerWhoReachesNothing_Gets403_AndNoOrgGets404()
    {
        _scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostScope?)null);
        Assert.IsType<ForbidResult>((await Overview().Get(CancellationToken.None)).Result);

        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await Overview().Get(CancellationToken.None)).Result).StatusCode);

        _overview.VerifyNoOtherCalls();
    }

    [Fact]
    public void OverviewController_IsBehindTheFlag_AndThePolicyOfTheTeam()
    {
        var controller = typeof(OrgOverviewController);

        Assert.NotEmpty(controller.GetCustomAttributes(typeof(FeatureGateAttribute), inherit: true));
        Assert.Equal(
            CasazenPolicies.OrgMembersManage,
            controller.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy);
        Assert.Equal("api/orgs/me/overview", controller.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Cast<RouteAttribute>().Single().Template);
    }
}
