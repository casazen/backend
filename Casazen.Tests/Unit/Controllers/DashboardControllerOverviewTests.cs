using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
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
/// SR-03b on <c>GET /api/dashboard/overview</c>: the words of the period (a free period needs both its days, in order, at most 366
/// of them), what the endpoint asks the service, and above all the money: for a caller without <c>payment.read</c> the answer has
/// no revenue, cash or chart at all (SR-D7), and <c>kpis</c> still does not know a free period.
/// </summary>
public class DashboardControllerOverviewTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly HostScope Scope = new(OrgId, "auth0|owner_123");

    private readonly Mock<IHostDashboardService> _dashboard = new();

    private DashboardController CreateController(bool canReadPayments = true, bool hasOrg = true)
    {
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(hasOrg ? OrgId : null);
        return new DashboardController(
            _dashboard.Object,
            Mock.Of<IHostTodayService>(),
            org.Object,
            HostAuthorizationTestHarness.ScopeResolver(),
            HostAuthorizationTestHarness.Create(OrgId, (_, permission) => permission != "payment.read" || canReadPayments))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User("auth0|owner_123"),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static HostOverview Overview(bool withMoney)
    {
        var period = HostDashboardPeriod.ForMonth(new DateOnly(2026, 10, 1));
        var today = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var occupancy = new HostOverviewOccupancy(new HostDashboardOccupancy(11, 62, 0), 9.7m);
        var stay = new HostDashboardStay(
            Guid.NewGuid(), Guid.NewGuid(), "Trullo", "Anna Verdi", today.AddDays(1), today.AddDays(4), BookingStatus.Pending, 600m, today)
        {
            BookingCode = "ABCDEFGHJK",
            Source = BookingSource.Direct,
            NumberOfGuests = 2,
        };
        var series = new HostOverviewSeries(
            HostOverviewSeriesUnit.Week,
            [new HostOverviewBucket("2026-W41", new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), 500m, 0m, false)]);

        return new HostOverview(
            period,
            today,
            2,
            occupancy,
            withMoney ? new HostOverviewRevenue(600m, 2, 20.0m) : null,
            withMoney ? new HostDashboardCollected(450m, 2) : null,
            new HostDashboardDirectShare(1, 2),
            new HostOverviewUpcoming(3, 1, 1),
            [stay],
            new HostOverviewPrevious(
                period.Previous(),
                new HostOverviewOccupancy(new HostDashboardOccupancy(5, 62, 0), null),
                withMoney ? new HostOverviewRevenue(500m, 1, null) : null,
                withMoney ? new HostDashboardCollected(500m, 1) : null,
                new HostDashboardDirectShare(1, 1)),
            withMoney ? series : null);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // --- The words of the period ---------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null, null, null, true, HostDashboardPeriodKind.Month)]
    [InlineData("Month", "2026-10", null, null, true, HostDashboardPeriodKind.Month)]
    [InlineData("last30days", null, null, null, true, HostDashboardPeriodKind.Last30Days)]
    [InlineData("Next30Days", null, null, null, true, HostDashboardPeriodKind.Next30Days)]
    [InlineData("Custom", null, "2026-10-01", "2026-10-31", true, HostDashboardPeriodKind.Custom)]
    [InlineData("custom", null, "2026-10-09", "2026-10-09", true, HostDashboardPeriodKind.Custom)]
    [InlineData("Custom", null, "2026-01-01", "2027-01-01", true, HostDashboardPeriodKind.Custom)]
    [InlineData("Custom", null, null, null, false, HostDashboardPeriodKind.Custom)] // no days
    [InlineData("Custom", null, "2026-10-01", null, false, HostDashboardPeriodKind.Custom)] // one day only
    [InlineData("Custom", null, null, "2026-10-31", false, HostDashboardPeriodKind.Custom)]
    [InlineData("Custom", null, "2026-10-31", "2026-10-01", false, HostDashboardPeriodKind.Custom)] // out of order
    [InlineData("Custom", null, "2026-01-01", "2027-01-02", false, HostDashboardPeriodKind.Custom)] // 367 days
    [InlineData("Custom", null, "1999-12-01", "2000-01-31", false, HostDashboardPeriodKind.Custom)] // before the supported years
    [InlineData("Custom", "2026-10", "2026-10-01", "2026-10-31", false, HostDashboardPeriodKind.Custom)] // a month and days
    [InlineData("Month", null, "2026-10-01", "2026-10-31", false, HostDashboardPeriodKind.Month)] // days with a period that has none
    [InlineData("Next30Days", null, "2026-10-01", null, false, HostDashboardPeriodKind.Next30Days)]
    [InlineData("Last30Days", "2026-10", null, null, false, HostDashboardPeriodKind.Last30Days)] // a month with another period
    [InlineData("Month", "2026-13", null, null, false, HostDashboardPeriodKind.Month)]
    [InlineData("Next60Days", null, null, null, false, HostDashboardPeriodKind.Month)]
    [InlineData("3", null, null, null, false, HostDashboardPeriodKind.Month)] // a number is no name
    public void TryParseOverviewPeriod_TheKindsOfKpisAndAFreePeriodWithItsDays(
        string? period, string? month, string? from, string? to, bool accepted, HostDashboardPeriodKind kind)
    {
        var ok = DashboardController.TryParseOverviewPeriod(
            period, month, from is null ? null : DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to), out var parsed, out var monthStart);

        Assert.Equal(accepted, ok);
        if (accepted)
        {
            Assert.Equal(kind, parsed);
            Assert.Equal(month is null || kind != HostDashboardPeriodKind.Month ? null : DateOnly.Parse(month + "-01"), monthStart);
        }
    }

    [Theory]
    [InlineData("Custom")]
    [InlineData("custom")]
    public void Kpis_StillDoesNotKnowAFreePeriod_ItWouldNeverHaveItsDays(string period)
    {
        Assert.False(DashboardController.TryParsePeriod(period, null, out _, out _));
        Assert.True(DashboardController.TryParsePeriod("Next30Days", null, out var kind, out _));
        Assert.Equal(HostDashboardPeriodKind.Next30Days, kind);
        Assert.True(DashboardController.TryParsePeriod(null, null, out var byDefault, out _));
        Assert.Equal(HostDashboardPeriodKind.Month, byDefault);
    }

    [Fact]
    public async Task Overview_AnInvalidPeriod_Is400WithTheCodeOfEveryPeriod_AndAsksTheServiceNothing()
    {
        var result = await CreateController().GetOverview("Custom", null, new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 1), null, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(DashboardController.InvalidPeriodCode, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        _dashboard.Verify(d => d.GetOverviewAsync(It.IsAny<HostScope>(), It.IsAny<HostOverviewQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Overview_ACallerWithNoOrg_IsForbidden_NotShownAnotherOrgsNumbers()
    {
        var result = await CreateController(hasOrg: false).GetOverview(null, null, null, null, null, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _dashboard.Verify(d => d.GetOverviewAsync(It.IsAny<HostScope>(), It.IsAny<HostOverviewQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- What the service is asked, and the money -------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overview_TheMoneyIsAskedForOnlyForACallerWhoMayReadPayments(bool canReadPayments)
    {
        var propertyId = Guid.NewGuid();
        _dashboard
            .Setup(d => d.GetOverviewAsync(It.IsAny<HostScope>(), It.IsAny<HostOverviewQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Overview(canReadPayments));

        await CreateController(canReadPayments).GetOverview(
            "Custom", null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), propertyId, CancellationToken.None);

        _dashboard.Verify(
            d => d.GetOverviewAsync(
                Scope,
                new HostOverviewQuery(HostDashboardPeriodKind.Custom, null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), propertyId, canReadPayments),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Overview_WithPaymentRead_TheAnswerHasTheRevenueTheCashAndTheChart_EachSayingWhatItCounts()
    {
        _dashboard
            .Setup(d => d.GetOverviewAsync(It.IsAny<HostScope>(), It.IsAny<HostOverviewQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Overview(withMoney: true));

        var result = await CreateController().GetOverview(null, "2026-10", null, null, null, CancellationToken.None);

        var dto = Assert.IsType<HostOverviewDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));
        var root = json.RootElement;
        Assert.Equal(("accrual", 600m, 60_000L, 20.0m), (
            root.GetProperty("revenue").GetProperty("basis").GetString(),
            root.GetProperty("revenue").GetProperty("amount").GetDecimal(),
            root.GetProperty("revenue").GetProperty("amountCents").GetInt64(),
            root.GetProperty("revenue").GetProperty("deltaPercent").GetDecimal()));
        Assert.Equal(("cash", 450m), (root.GetProperty("collected").GetProperty("basis").GetString(), root.GetProperty("collected").GetProperty("amount").GetDecimal()));
        Assert.Equal(9.7m, root.GetProperty("occupancy").GetProperty("deltaPoints").GetDecimal());
        Assert.Equal(("week", 1), (root.GetProperty("series").GetProperty("unit").GetString(), root.GetProperty("series").GetProperty("buckets").GetArrayLength()));
        var bucket = root.GetProperty("series").GetProperty("buckets")[0];
        Assert.Equal(("2026-W41", "2026-10-05", "2026-10-11", 500m, 50_000L, false),
            (bucket.GetProperty("label").GetString(), bucket.GetProperty("from").GetString(), bucket.GetProperty("to").GetString(),
             bucket.GetProperty("amount").GetDecimal(), bucket.GetProperty("amountCents").GetInt64(), bucket.GetProperty("isFuture").GetBoolean()));
        Assert.Equal(500m, root.GetProperty("previous").GetProperty("revenue").GetProperty("amount").GetDecimal());
        Assert.Equal((3, 1, 1), (
            root.GetProperty("upcoming").GetProperty("count").GetInt32(),
            root.GetProperty("upcoming").GetProperty("previousCount").GetInt32(),
            root.GetProperty("upcoming").GetProperty("pendingApproval").GetInt32()));
        var arrival = root.GetProperty("nextArrivals")[0];
        Assert.Equal(("ABCDE-FGHJK", true), (arrival.GetProperty("bookingCode").GetString(), arrival.GetProperty("awaitingApproval").GetBoolean()));
        // No money on an arrival: who comes, not what they pay.
        Assert.False(arrival.TryGetProperty("totalPrice", out _));
    }

    [Fact]
    public async Task Overview_WithoutPaymentRead_TheMoneyIsNotInTheAnswerAtAll_NotEvenAsNull()
    {
        _dashboard
            .Setup(d => d.GetOverviewAsync(It.IsAny<HostScope>(), It.IsAny<HostOverviewQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Overview(withMoney: false));

        var result = await CreateController(canReadPayments: false).GetOverview(null, null, null, null, null, CancellationToken.None);

        var dto = Assert.IsType<HostOverviewDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var text = JsonSerializer.Serialize(dto, Web);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        foreach (var money in new[] { "revenue", "collected", "series" })
            Assert.False(root.TryGetProperty(money, out _), $"{money} is in the answer");
        var previous = root.GetProperty("previous");
        Assert.False(previous.TryGetProperty("revenue", out _));
        Assert.False(previous.TryGetProperty("collected", out _));
        Assert.DoesNotContain("amount", text, StringComparison.OrdinalIgnoreCase);
        // What is not money is there.
        Assert.Equal(9.7m, root.GetProperty("occupancy").GetProperty("deltaPoints").GetDecimal());
        Assert.Equal(JsonValueKind.Null, previous.GetProperty("occupancy").GetProperty("deltaPoints").ValueKind);
        Assert.Equal(3, root.GetProperty("upcoming").GetProperty("count").GetInt32());
        Assert.Single(root.GetProperty("nextArrivals").EnumerateArray());
        Assert.Equal(2, root.GetProperty("directShare").GetProperty("stays").GetInt32());
    }
}
