using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
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
/// NF-98: the endpoint of the channel costs report. Money, so <c>payment.read</c> on the class and nothing anonymous; the scope is the caller's own; the words of the
/// request are checked, with the codes of the other reports, before anything is read; a property outside the scope is the service's 404; the answer is JSON (a cost
/// nobody reported is null) or a CSV file named with its base and its days.
/// </summary>
public class ChannelCostsReportsControllerTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly DateOnly First = new(2026, 10, 1);
    private static readonly DateOnly Last = new(2026, 10, 31);
    private static readonly HostScope Scope = new(OrgId, GrantedToUserId: "auth0|collaboratore");

    private readonly Mock<IChannelCostsReportService> _reports = new();
    private readonly Mock<IOrgContextResolver> _org = new();
    private readonly Mock<IHostScopeResolver> _scopes = new();

    public ChannelCostsReportsControllerTests()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _scopes
            .Setup(s => s.ResolveAsync("auth0|collaboratore", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scope);
    }

    private ChannelCostsReportsController Controller(string? sub = "auth0|collaboratore")
    {
        var claims = sub is null ? [] : new[] { new Claim("sub", sub) };
        return new ChannelCostsReportsController(_reports.Object, _org.Object, _scopes.Object)
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

    private static ChannelCostsReport Report()
    {
        var stays = new[]
        {
            new ChannelCostStay(BookingSource.Airbnb, 200m, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), 24m, null),
            new ChannelCostStay(BookingSource.Expedia, 300m, new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), null, null),
        };
        return ChannelCostRules.Report(HostReportRules.PeriodOf(First, Last), stays, new Dictionary<BookingSource, ChannelCommissionMode>());
    }

    private static (int? Status, string? Code) Problem(IActionResult result)
    {
        var problem = Assert.IsAssignableFrom<ObjectResult>(result);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        return (problem.StatusCode, details.Extensions[ApiProblemDetails.CodeExtension] as string);
    }

    [Fact]
    public void TheController_NeedsPaymentRead_OnTheClass_AndNothingIsAnonymous()
    {
        var type = typeof(ChannelCostsReportsController);

        var policies = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy).ToArray();

        Assert.Contains(CasazenPolicies.PaymentRead, policies);
        Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("api/reports/channel-costs", type.GetCustomAttribute<RouteAttribute>()!.Template);
        var action = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(action.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public async Task ChannelCosts_ReadsWithTheScopeOfTheCaller_AndAnswersJsonWithTheCostsThatAreNotAvailableAsNull()
    {
        var propertyId = Guid.NewGuid();
        _reports
            .Setup(r => r.GetChannelCostsAsync(Scope, new HostReportQuery(First, Last, propertyId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Report());

        var result = await Controller().ChannelCosts(First, Last, propertyId, null, CancellationToken.None);

        var dto = Assert.IsType<ChannelCostsReportDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(ChannelCostsReportDocuments.Title, dto.Title);
        Assert.Equal("accrual", dto.Basis);
        var airbnb = dto.Rows.Single(r => r.Source == "Airbnb");
        Assert.Equal((200m, 20000L, 24m, 2400L, 0.12m), (airbnb.Gross, airbnb.GrossCents, airbnb.Commission, airbnb.CommissionCents, airbnb.CostRate));
        Assert.Equal("ChannelCommission", airbnb.Kind);
        Assert.Equal("Unknown", airbnb.CommissionMode);
        Assert.True(airbnb.MostExpensive);
        // A cost nobody reported is null in every form, never zero.
        var expedia = dto.Rows.Single(r => r.Source == "Expedia");
        Assert.Equal(300m, expedia.Gross);
        Assert.Null(expedia.Commission);
        Assert.Null(expedia.CommissionCents);
        Assert.Null(expedia.Net);
        Assert.Null(expedia.NetCents);
        Assert.Null(expedia.CostRate);
        Assert.Equal(1, expedia.StaysWithoutCommission);
        Assert.Null(dto.Total.Source);
        Assert.Null(dto.Total.Kind);
        Assert.Equal(500m, dto.Total.Gross);
    }

    [Theory]
    [InlineData("2026-10-01", null, null, 400, "report_invalid_period")]
    [InlineData(null, "2026-10-31", null, 400, "report_invalid_period")]
    [InlineData("2026-10-31", "2026-10-01", null, 400, "report_invalid_period")]
    [InlineData("2026-10-01", "2026-10-31", "xml", 400, "report_invalid_format")]
    [InlineData("2024-01-01", "2026-01-01", null, 422, "report_period_too_long")]
    public async Task ChannelCosts_AWrongRequest_IsAProblemBeforeAnythingIsRead(string? from, string? to, string? format, int status, string code)
    {
        var result = await Controller().ChannelCosts(
            from is null ? null : DateOnly.Parse(from, CultureInfo.InvariantCulture),
            to is null ? null : DateOnly.Parse(to, CultureInfo.InvariantCulture),
            null,
            format,
            CancellationToken.None);

        Assert.Equal((status, code), Problem(result));
        _reports.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChannelCosts_AsCsv_IsAFileNamedWithItsBaseAndItsDays()
    {
        _reports
            .Setup(r => r.GetChannelCostsAsync(Scope, new HostReportQuery(First, Last, null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Report());

        var result = await Controller().ChannelCosts(First, Last, null, "csv", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(("text/csv", "casazen-costi-dei-canali-competenza-20261001-20261031.csv"), (file.ContentType, file.FileDownloadName));
        Assert.NotEmpty(file.FileContents);
    }

    [Fact]
    public async Task ChannelCosts_ACallerWithoutAnOrgOrAScope_Is403_AndNothingIsRead()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        Assert.Equal(StatusCodes.Status403Forbidden, Problem(await Controller().ChannelCosts(First, Last, null, null, CancellationToken.None)).Status);

        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        Assert.Equal(StatusCodes.Status403Forbidden, Problem(await Controller("auth0|sconosciuto").ChannelCosts(First, Last, null, null, CancellationToken.None)).Status);
        Assert.Equal(StatusCodes.Status403Forbidden, Problem(await Controller(null).ChannelCosts(First, Last, null, null, CancellationToken.None)).Status);

        _reports.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChannelCosts_APropertyOutsideTheScope_IsTheServicesNotFound_NeverAnAnswer()
    {
        var elsewhere = Guid.NewGuid();
        _reports
            .Setup(r => r.GetChannelCostsAsync(Scope, new HostReportQuery(First, Last, elsewhere), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("Property not found") { MessageKey = "PropertyNotFound" });

        await Assert.ThrowsAsync<NotFoundException>(() => Controller().ChannelCosts(First, Last, elsewhere, null, CancellationToken.None));
    }
}
