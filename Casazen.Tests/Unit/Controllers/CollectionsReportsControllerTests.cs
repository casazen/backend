using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// NF-97: the endpoints of the collections report. Money, so <c>payment.read</c> on the class and nothing anonymous; the scope is the caller's own (never one
/// from the request); the words of the request are checked, with the codes of the other reports, before anything is read; a property outside the scope is the
/// service's 404; the answer is JSON or a CSV file named with its base and its days.
/// </summary>
public class CollectionsReportsControllerTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly DateOnly First = new(2026, 10, 1);
    private static readonly DateOnly Last = new(2026, 10, 31);
    private static readonly HostScope Scope = new(OrgId, GrantedToUserId: "auth0|collaboratore");

    private readonly Mock<IHostCollectionsReportService> _reports = new();
    private readonly Mock<IOrgContextResolver> _org = new();
    private readonly Mock<IHostScopeResolver> _scopes = new();

    public CollectionsReportsControllerTests()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _scopes
            .Setup(s => s.ResolveAsync("auth0|collaboratore", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scope);
    }

    private CollectionsReportsController Controller(string? sub = "auth0|collaboratore")
    {
        var claims = sub is null ? [] : new[] { new Claim("sub", sub) };
        return new CollectionsReportsController(_reports.Object, _org.Object, _scopes.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static HostCollectionsReport EmptyReport() => new(
        HostReportRules.PeriodOf(First, Last),
        new HostDashboardCollected(0m, 0),
        HostCollectionsRules.EmptyBucketsOf(HostReportRules.PeriodOf(First, Last)),
        [],
        [],
        new HostCredits(new HostStripeCredits(false, [], 0, 0), new HostChannelCredits(false, [], 0)));

    private static (int? Status, string? Code) Problem(IActionResult result)
    {
        var problem = Assert.IsAssignableFrom<ObjectResult>(result);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        return (problem.StatusCode, details.Extensions[ApiProblemDetails.CodeExtension] as string);
    }

    // ---------------------------------------------------------------- the wiring

    [Fact]
    public void TheController_NeedsPaymentRead_OnTheClass_AndNothingIsAnonymous()
    {
        var type = typeof(CollectionsReportsController);

        var policies = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy).ToArray();

        Assert.Contains(CasazenPolicies.PaymentRead, policies);
        Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("api/reports", type.GetCustomAttribute<RouteAttribute>()!.Template);
        foreach (var action in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());

        Assert.Equal("collections", type.GetMethod(nameof(CollectionsReportsController.Collections))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("balances-to-collect", type.GetMethod(nameof(CollectionsReportsController.BalancesToCollect))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }

    // ---------------------------------------------------------------- the collections

    [Fact]
    public async Task Collections_ReadsWithTheScopeOfTheCaller_AndAnswersJson()
    {
        var propertyId = Guid.NewGuid();
        _reports
            .Setup(r => r.GetCollectionsAsync(Scope, new HostReportQuery(First, Last, propertyId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyReport());

        var result = await Controller().Collections(First, Last, propertyId, null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<CollectionsReportDto>(ok.Value);
        Assert.Equal(HostCollectionsReportDocuments.CollectionsTitle, dto.Title);
        Assert.Equal("cash", dto.Basis);
        _reports.Verify(r => r.GetCollectionsAsync(Scope, new HostReportQuery(First, Last, propertyId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("2026-10-01", null, null, 400, "report_invalid_period")]
    [InlineData(null, "2026-10-31", null, 400, "report_invalid_period")]
    [InlineData("2026-10-31", "2026-10-01", null, 400, "report_invalid_period")]
    [InlineData("2026-10-01", "2026-10-31", "pdf", 400, "report_invalid_format")]
    [InlineData("2024-01-01", "2026-01-01", null, 422, "report_period_too_long")]
    public async Task Collections_AWrongRequest_IsAProblemBeforeAnythingIsRead(string? from, string? to, string? format, int status, string code)
    {
        var result = await Controller().Collections(
            from is null ? null : DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture),
            null,
            format,
            CancellationToken.None);

        Assert.Equal((status, code), Problem(result));
        _reports.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Collections_AsCsv_IsAFileNamedWithItsBaseAndItsDays()
    {
        _reports
            .Setup(r => r.GetCollectionsAsync(Scope, new HostReportQuery(First, Last, null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyReport());

        var result = await Controller().Collections(First, Last, null, "CSV", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal("casazen-incassato-per-cassa-20261001-20261031.csv", file.FileDownloadName);
        Assert.NotEmpty(file.FileContents);
    }

    [Fact]
    public async Task Collections_ACallerWithoutAnOrgOrAScope_Is403_AndNothingIsRead()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        Assert.Equal(StatusCodes.Status403Forbidden, Problem(await Controller().Collections(First, Last, null, null, CancellationToken.None)).Status);

        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        Assert.Equal(StatusCodes.Status403Forbidden, Problem(await Controller("auth0|sconosciuto").Collections(First, Last, null, null, CancellationToken.None)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, Problem(await Controller(null).BalancesToCollect(null, null, CancellationToken.None)).Status);

        _reports.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Collections_APropertyOutsideTheScope_IsTheServicesNotFound_NeverAnAnswer()
    {
        var elsewhere = Guid.NewGuid();
        _reports
            .Setup(r => r.GetCollectionsAsync(Scope, new HostReportQuery(First, Last, elsewhere), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("Property not found") { MessageKey = "PropertyNotFound" });

        await Assert.ThrowsAsync<NotFoundException>(() => Controller().Collections(First, Last, elsewhere, null, CancellationToken.None));
    }

    // ---------------------------------------------------------------- the balances

    private static HostBalancesToCollect NoBalances() => new(new DateOnly(2026, 10, 9), [], []);

    [Fact]
    public async Task BalancesToCollect_ReadsWithTheScopeOfTheCaller_AndAnswersJsonOrACsvFile()
    {
        var propertyId = Guid.NewGuid();
        _reports
            .Setup(r => r.GetBalancesToCollectAsync(Scope, new HostBalancesQuery(propertyId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NoBalances());

        var json = await Controller().BalancesToCollect(propertyId, "json", CancellationToken.None);
        var csv = await Controller().BalancesToCollect(propertyId, "csv", CancellationToken.None);

        var dto = Assert.IsType<BalancesToCollectReportDto>(Assert.IsType<OkObjectResult>(json).Value);
        Assert.Equal(new DateOnly(2026, 10, 9), dto.AsOf);
        Assert.Equal("balances", dto.Basis);
        var file = Assert.IsType<FileContentResult>(csv);
        Assert.Equal(("text/csv", "casazen-saldi-da-incassare-20261009.csv"), (file.ContentType, file.FileDownloadName));
    }

    [Fact]
    public async Task BalancesToCollect_AWrongFormat_IsAProblemBeforeAnythingIsRead()
    {
        var result = await Controller().BalancesToCollect(null, "xml", CancellationToken.None);

        Assert.Equal((400, "report_invalid_format"), Problem(result));
        _reports.VerifyNoOtherCalls();
    }
}
