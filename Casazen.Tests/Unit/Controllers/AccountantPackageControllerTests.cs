using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E15/F01/US01/T01 (NF-101, NF-87, NF-115): what the endpoints of the accountant package decide before anything is read, which is where the permission rule lives. The owner accounting is
/// asked of the service only when the caller holds <c>agency.statement.view</c> (the policy is evaluated by the controller and becomes the <see cref="AccountantPackageAccess"/>), a caller
/// whose org or scope cannot be resolved is a 403 and never the whole org, the period is a month or a year or a 400, and the doors (the flag, <c>payment.read</c>) are on the class.
/// </summary>
public class AccountantPackageControllerTests
{
    private const string UserId = "auth0|commercialista";

    private static readonly Guid OrgId = Guid.NewGuid();

    private sealed class Harness
    {
        public Harness(bool hasOrg = true, bool hasScope = true, bool holdsStatementView = true, HostScope? scope = null)
        {
            Scope = scope ?? new HostScope(OrgId);
            var orgs = new Mock<IOrgContextResolver>();
            orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(hasOrg ? OrgId : (Guid?)null);
            var scopes = new Mock<IHostScopeResolver>();
            scopes
                .Setup(s => s.ResolveAsync(UserId, It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(hasScope ? Scope : null);
            Authorization
                .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.AgencyStatementView))
                .ReturnsAsync(holdsStatementView ? AuthorizationResult.Success() : AuthorizationResult.Failed());

            Http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId)], "test")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            };
            Controller = new AccountantPackageController(
                Packages.Object, Summaries.Object, Mock.Of<IAccountantPackageSettingsService>(), Delivery.Object, orgs.Object, scopes.Object, Authorization.Object, Log)
            {
                ControllerContext = new ControllerContext { HttpContext = Http },
            };
        }

        public HostScope Scope { get; }

        public Mock<IAccountantPackageService> Packages { get; } = new();

        public Mock<IAgencySummaryService> Summaries { get; } = new();

        public Mock<IAccountantPackageDeliveryService> Delivery { get; } = new();

        public Mock<IAuthorizationService> Authorization { get; } = new();

        public CapturingLogger<AccountantPackageController> Log { get; } = new();

        public HttpContext Http { get; }

        public AccountantPackageController Controller { get; }

        /// <summary>The access, the period and the destination the service was given by the next call of <c>BuildAsync</c>.</summary>
        public Captured CaptureBuild()
        {
            var captured = new Captured();
            Packages
                .Setup(p => p.BuildAsync(Scope, It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Callback<HostScope, AccountantPackageAccess, AccountantPackagePeriod, Stream, CancellationToken>((_, access, period, destination, _) =>
                {
                    captured.Access = access;
                    captured.Period = period;
                    captured.Destination = destination;
                })
                .ReturnsAsync(new AccountantPackageResult(3, 12, 4096, []));
            return captured;
        }

        /// <summary>The access and the period the service was given by the next call of <c>GetAsync</c>; it answers a summary that says whether the owner accounting is in it.</summary>
        public Captured CaptureSummary()
        {
            var captured = new Captured();
            Summaries
                .Setup(s => s.GetAsync(Scope, It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<CancellationToken>()))
                .Returns((HostScope _, AccountantPackageAccess access, AccountantPackagePeriod period, CancellationToken _) =>
                {
                    captured.Access = access;
                    captured.Period = period;
                    return Task.FromResult(EmptySummary(period, access.AllowsOwnerAccounting(Scope)));
                });
            return captured;
        }
    }

    private sealed class Captured
    {
        public AccountantPackageAccess? Access { get; set; }

        public AccountantPackagePeriod? Period { get; set; }

        public Stream? Destination { get; set; }
    }

    private static AgencySummary EmptySummary(AccountantPackagePeriod period, bool ownerAccountingIncluded) =>
        new(
            period,
            new DateOnly(2026, 11, 14),
            ownerAccountingIncluded,
            new AgencyStaysFigures(0, 0, null, 0m, 0, 0, null, null),
            new AgencyCollectionsFigures(0m, 0, []),
            [],
            [],
            new AgencyTouristTax(new AgencyItalianTouristTax(0m, null, null, 0), []),
            [],
            new AgencyOwnerInvoices([], []),
            [],
            [],
            [],
            [],
            []);

    private static string CodeOf(IActionResult result)
    {
        var problem = Assert.IsAssignableFrom<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        return (string)problem.Extensions["code"]!;
    }

    private static int StatusOf(IActionResult result) => Assert.IsType<ObjectResult>(result).StatusCode!.Value;

    // ---------------------------------------------------------------- the permission rule

    /// <summary>
    /// PROOF TEST: permission rule. The owner accounting is asked of the service for a caller that holds <c>agency.statement.view</c> and only then: the controller evaluates the policy
    /// by its name and turns the answer into the access the service gets, for the ZIP and for the summary alike. The service gets the days, the scope of the caller and the response
    /// as the destination.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Download_TheStatementPermission_BecomesTheAccessTheServiceGets(bool holdsStatementView)
    {
        var harness = new Harness(holdsStatementView: holdsStatementView);
        var captured = harness.CaptureBuild();

        var result = await harness.Controller.Download("2026-10", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(new AccountantPackageAccess(holdsStatementView), captured.Access);
        Assert.Equal(holdsStatementView ? AccountantPackageAccess.Full : AccountantPackageAccess.WithoutOwnerAccounting, captured.Access);
        Assert.Equal("casazen-commercialista-2026-10.zip", captured.Period!.FileName);
        Assert.Same(harness.Http.Response.Body, captured.Destination);
        harness.Authorization.Verify(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.AgencyStatementView), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAgencySummary_TheStatementPermission_BecomesTheAccessTheServiceGets_AndTheDtoSaysIt(bool holdsStatementView)
    {
        var harness = new Harness(holdsStatementView: holdsStatementView);
        var captured = harness.CaptureSummary();

        var result = await harness.Controller.GetAgencySummary("2026", CancellationToken.None);

        var dto = Assert.IsType<AgencySummaryDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(new AccountantPackageAccess(holdsStatementView), captured.Access);
        Assert.Equal(("2026", "year", holdsStatementView), (dto.Period, dto.PeriodKind, dto.OwnerAccountingIncluded));
        Assert.Equal("private, no-store", harness.Http.Response.Headers.CacheControl.ToString());
    }

    /// <summary>The permission does not widen the reach: a caller that holds it but reaches some properties is given the access, and its own restricted scope, to the service (which leaves the owner accounting out).</summary>
    [Fact]
    public async Task Download_ARestrictedScope_IsHandedOverAsItIs_NeverWidened()
    {
        var restricted = new HostScope(OrgId, GrantedToUserId: UserId);
        var harness = new Harness(scope: restricted);
        var captured = harness.CaptureBuild();

        await harness.Controller.Download("2026-10", CancellationToken.None);

        harness.Packages.Verify(p => p.BuildAsync(restricted, It.IsAny<AccountantPackageAccess>(), It.IsAny<AccountantPackagePeriod>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(captured.Access!.AllowsOwnerAccounting(restricted));
    }

    // ---------------------------------------------------------------- who is nobody, and what is not a period

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BothEndpoints_ACallerWithNoOrgOrNoScope_Is403_AndNothingIsReadOrDecided(bool hasOrg, bool hasScope)
    {
        var harness = new Harness(hasOrg, hasScope);

        var zip = await harness.Controller.Download("2026-10", CancellationToken.None);
        var summary = await harness.Controller.GetAgencySummary("2026-10", CancellationToken.None);

        Assert.All(new[] { zip, summary }, result =>
        {
            Assert.Equal((StatusCodes.Status403Forbidden, ProblemCodes.Forbidden), (StatusOf(result), CodeOf(result)));
        });
        harness.Packages.VerifyNoOtherCalls();
        harness.Summaries.VerifyNoOtherCalls();
        harness.Authorization.Verify(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("1999")]
    [InlineData("2026-10-01")]
    [InlineData("12 months")]
    public async Task BothEndpoints_AMissingOrMalformedPeriod_Is400_AndNothingIsReadOrDecided(string? period)
    {
        var harness = new Harness();

        var zip = await harness.Controller.Download(period, CancellationToken.None);
        var summary = await harness.Controller.GetAgencySummary(period, CancellationToken.None);

        Assert.All(new[] { zip, summary }, result =>
        {
            Assert.Equal((StatusCodes.Status400BadRequest, AccountantPackageErrors.InvalidPeriod), (StatusOf(result), CodeOf(result)));
        });
        harness.Packages.VerifyNoOtherCalls();
        harness.Summaries.VerifyNoOtherCalls();
        harness.Authorization.Verify(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()), Times.Never);
    }

    // ---------------------------------------------------------------- the response and the log

    /// <summary>PROOF TEST: privacy rule. The package is money and personal data built on request: never cached, and the log line carries the ids of the org and of the user and the counts, nothing else.</summary>
    [Fact]
    public async Task Download_IsNeverCached_AndWritesOneLineWithTheIdsAndTheCounts()
    {
        var harness = new Harness();
        harness.CaptureBuild();

        await harness.Controller.Download("2026-10", CancellationToken.None);

        Assert.Equal("private, no-store", harness.Http.Response.Headers.CacheControl.ToString());
        var line = Assert.Single(harness.Log.Entries);
        Assert.Equal(OrgId, line.Values["OrgId"]);
        Assert.Equal(UserId, line.Values["UserId"]);
        Assert.Equal((3, 12, 4096L, 0), ((int)line.Values["Documents"]!, (int)line.Values["Files"]!, (long)line.Values["Bytes"]!, (int)line.Values["OmittedFolders"]!));
    }

    // ---------------------------------------------------------------- the doors

    /// <summary>
    /// PROOF TEST: permission rule and rollback. Every endpoint is behind <c>Features:AccountantPackage</c> (404 while it is off: unsetting the variable is the rollback) and
    /// <c>payment.read</c> on the class, nothing is anonymous; the two reads of the package (the ZIP and the summary) are plain GETs with no policy of their own, and what changes something
    /// (the settings of the send, E15/F01/US02) asks <c>property.write</c> on top, so a caller that only reads the money (the accountant) never changes it. The owner accounting is a
    /// permission the controller evaluates, not a requirement of the class (a caller without it still gets the rest of the package).
    /// </summary>
    [Fact]
    public void TheController_IsBehindTheFlagAndPaymentRead_NothingIsAnonymous_AndOnlyTheSettingsChangeAnything()
    {
        var controller = typeof(AccountantPackageController);

        Assert.Equal(FeatureFlags.AccountantPackage, Assert.Single(controller.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.Equal([CasazenPolicies.PaymentRead], controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("api/accountant-package", controller.GetCustomAttribute<RouteAttribute>()!.Template);

        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(5, actions.Length);
        foreach (var action in actions)
        {
            Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Empty(action.GetCustomAttributes<FeatureGateAttribute>());
            var methods = action.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).Distinct().ToList();
            if (methods.Contains("GET"))
                Assert.DoesNotContain(action.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy != null);
            else
                Assert.Equal([CasazenPolicies.PropertyWrite], action.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));
        }

        Assert.Equal(
            ["Download:GET", "GetAgencySummary:GET", "GetSettings:GET", "SaveSettings:PUT", "Send:POST"],
            actions.Select(a => $"{a.Name}:{string.Join("/", a.GetCustomAttributes<HttpMethodAttribute>().SelectMany(m => m.HttpMethods))}").Order());
    }

    // ---------------------------------------------------------------- «send a test» (E15/F01/US02/T02)

    /// <summary>
    /// PROOF TEST: permission rule. The owner accounting is in the package a person sends to the accountant only if that person holds <c>agency.statement.view</c>: the controller evaluates the policy by its name and
    /// turns the answer into the access the service gets, so a person never shares with a third party what it may not read. The answer says the month and the expiry of the link, never the address or the link.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Send_TheStatementPermission_BecomesTheAccessTheServiceGets_AndTheAnswerHoldsNoAddressAndNoLink(bool holdsStatementView)
    {
        var harness = new Harness(holdsStatementView: holdsStatementView);
        harness.Delivery
            .Setup(d => d.SendNowAsync(OrgId, new AccountantPackageAccess(holdsStatementView), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountantPackageSendNowResult("2026-10", new DateTime(2026, 12, 3, 5, 30, 0, DateTimeKind.Utc)));

        var result = await harness.Controller.Send(CancellationToken.None);

        var dto = Assert.IsType<AccountantPackageEmailQueuedDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(("2026-10", true, new DateTime(2026, 12, 3, 5, 30, 0, DateTimeKind.Utc)), (dto.Period, dto.Queued, dto.LinkExpiresAt));
        Assert.Equal("private, no-store", harness.Http.Response.Headers.CacheControl.ToString());
        harness.Authorization.Verify(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.AgencyStatementView), Times.Once);
    }

    /// <summary>PROOF TEST: permission rule. Nothing is made or sent by a caller that reaches only some properties, or none, even with the permissions: the package is the organization's.</summary>
    [Theory]
    [InlineData("account-in-no-team")]
    [InlineData("collaborator")]
    [InlineData("no-scope")]
    [InlineData("no-org")]
    public async Task Send_ACallerThatDoesNotReachTheWholeOrg_Is403_AndNothingIsMadeOrSent(string caller)
    {
        var harness = caller switch
        {
            "account-in-no-team" => new Harness(scope: new HostScope(OrgId, OwnerId: UserId)),
            "collaborator" => new Harness(scope: new HostScope(OrgId, GrantedToUserId: UserId)),
            "no-scope" => new Harness(hasScope: false),
            _ => new Harness(hasOrg: false),
        };

        var result = await harness.Controller.Send(CancellationToken.None);

        Assert.Equal((StatusCodes.Status403Forbidden, ProblemCodes.Forbidden), (StatusOf(result), CodeOf(result)));
        harness.Delivery.VerifyNoOtherCalls();
        harness.Authorization.Verify(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()), Times.Never);
    }
}
