using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.OwnerPortal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// NF-96 on <c>GET /api/owner-portal/overview</c> and <c>/trend</c>: 401 without a valid owner claim, the words of the request answered
/// with the codes of the host's reports (nothing is read for a bad request), and what the service is asked: the owner of the token, never
/// one the client names.
/// </summary>
public class OwnerPortalControllerOverviewTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();

    private readonly Mock<IOwnerPortalService> _portal = new();

    private OwnerPortalController CreateController(bool withOwnerClaim = true)
    {
        var claims = withOwnerClaim ? new[] { new Claim(OwnerPortalClaims.OwnerId, OwnerId.ToString()) } : [];
        return new OwnerPortalController(_portal.Object, Options.Create(new Auth0Options()), NullLogger<OwnerPortalController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly OctoberEnd = new(2026, 10, 31);

    private static OwnerPortalRow Total() => new(
        HostPerformanceRules.TotalKey, null, HostPerformanceRules.TotalKey, OwnerPerformanceFigures.Zero, null);

    [Fact]
    public async Task OverviewAndTrend_WithoutAValidOwnerClaim_Are401_AndReadNothing()
    {
        var controller = CreateController(withOwnerClaim: false);

        Assert.IsType<UnauthorizedResult>(await controller.GetOverview(October, OctoberEnd, null, null, null, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.GetTrend(October, OctoberEnd, null, null, null, CancellationToken.None));

        _portal.Verify(
            p => p.GetOwnerOverviewAsync(It.IsAny<Guid>(), It.IsAny<OwnerOverviewQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        _portal.Verify(
            p => p.GetOwnerTrendAsync(It.IsAny<Guid>(), It.IsAny<OwnerOverviewQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null, "2026-10-31", null, null, StatusCodes.Status400BadRequest, ReportRequestChecks.InvalidPeriodCode)]
    [InlineData("2026-10-31", "2026-10-01", null, null, StatusCodes.Status400BadRequest, ReportRequestChecks.InvalidPeriodCode)]
    [InlineData("2024-01-01", "2026-01-01", null, null, StatusCodes.Status422UnprocessableEntity, ReportRequestChecks.PeriodTooLongCode)]
    [InlineData("2026-10-01", "2026-10-31", "channel", null, StatusCodes.Status400BadRequest, ReportRequestChecks.InvalidGroupByCode)]
    [InlineData("2026-10-01", "2026-10-31", "owner", null, StatusCodes.Status400BadRequest, ReportRequestChecks.InvalidGroupByCode)]
    [InlineData("2026-10-01", "2026-10-31", null, "lastYear", StatusCodes.Status400BadRequest, ReportRequestChecks.InvalidCompareCode)]
    public async Task OverviewAndTrend_TheWordsOfTheRequest_AreAnsweredWithTheCodesOfTheHostReports(
        string? from, string? to, string? groupBy, string? compare, int status, string code)
    {
        DateOnly? first = from is null ? null : DateOnly.Parse(from);
        DateOnly? last = to is null ? null : DateOnly.Parse(to);

        var overview = Assert.IsType<ObjectResult>(
            await CreateController().GetOverview(first, last, null, groupBy, compare, CancellationToken.None));
        var trend = Assert.IsType<ObjectResult>(
            await CreateController().GetTrend(first, last, null, groupBy, compare, CancellationToken.None));

        foreach (var problem in new[] { overview, trend })
        {
            Assert.Equal(status, problem.StatusCode);
            Assert.Equal(code, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        }

        _portal.Verify(
            p => p.GetOwnerOverviewAsync(It.IsAny<Guid>(), It.IsAny<OwnerOverviewQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        _portal.Verify(
            p => p.GetOwnerTrendAsync(It.IsAny<Guid>(), It.IsAny<OwnerOverviewQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Overview_AsksTheServiceForTheOwnerOfTheToken_WithTheWordsOfTheRequest()
    {
        var propertyId = Guid.NewGuid();
        var period = HostReportRules.PeriodOf(October, OctoberEnd);
        _portal
            .Setup(p => p.GetOwnerOverviewAsync(It.IsAny<Guid>(), It.IsAny<OwnerOverviewQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnerPortalOverview(period, HostReportRules.PreviousYearPeriodOf(October, OctoberEnd), [], Total()));

        var result = await CreateController().GetOverview(October, OctoberEnd, propertyId, "Property", "PreviousYear", CancellationToken.None);

        var dto = Assert.IsType<OwnerPortalOverviewDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("property", dto.GroupBy);
        Assert.Equal("Prestazioni per immobile, per competenza", dto.Title);
        Assert.NotNull(dto.ComparePeriod);
        var expected = new OwnerOverviewQuery(October, OctoberEnd, HostPerformanceGroupBy.Property, true) { PropertyId = propertyId };
        _portal.Verify(p => p.GetOwnerOverviewAsync(OwnerId, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Trend_AsksTheServiceForTheOwnerOfTheToken_AndAnswersTheWeeksOrTheMonths()
    {
        var period = HostReportRules.PeriodOf(October, OctoberEnd);
        var (unit, buckets) = HostReportRules.TrendBucketsOf(period, HostTrendBucketUnit.Auto, false);
        _portal
            .Setup(p => p.GetOwnerTrendAsync(It.IsAny<Guid>(), It.IsAny<OwnerOverviewQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnerPortalTrend(period, null, unit, buckets, []));

        var result = await CreateController().GetTrend(October, OctoberEnd, null, null, null, CancellationToken.None);

        var dto = Assert.IsType<OwnerPortalTrendDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("week", dto.Unit);
        Assert.Equal("none", dto.GroupBy);
        Assert.Equal(5, dto.Buckets.Count);
        Assert.Null(dto.ComparePeriod);
        var expected = new OwnerOverviewQuery(October, OctoberEnd);
        _portal.Verify(p => p.GetOwnerTrendAsync(OwnerId, expected, It.IsAny<CancellationToken>()), Times.Once);
    }
}
