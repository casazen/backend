using System.Reflection;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Verification;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public sealed class IdentityMetricsEndpointTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] PreviousMembers =
        ["from", "to", "ok", "review", "notOk", "operatorApproved", "operatorRejected", "operatorDecisions", "operatorShare"];

    private static IdentityVerificationMetrics Sample() => new(
        Opened: 500,
        Outcomes: new IdentityOutcomeCounts(425, 40, 10, 5, 3, 7, 2, 1, 4, 3),
        AutomationRate: IdentityRate.Of(425, 500),
        ShadowApprovalRate: IdentityRate.Of(450, 500),
        ShadowVsHuman: new IdentityShadowVsHuman(300, 2, 20, 50),
        LatencyMs: new IdentityPercentiles(850, 2100, 480),
        ReviewSeconds: new IdentityPercentiles(900, 7200, 70),
        Coverage: [new IdentityCoverageCell("it-cie-3.0", "web:ios-safari", 200, 180, 190, 0.9, 0.85, 0.93)]);

    private static string[] Members(JsonElement element) => element.EnumerateObject().Select(member => member.Name).ToArray();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GetMetrics_WithAFlagOff_IsExactlyThePreviousAnswer(bool verification, bool selfHosted)
    {
        var flags = new List<string>();
        if (verification)
            flags.Add(FeatureFlags.CheckInIdentityVerification);
        if (selfHosted)
            flags.Add(FeatureFlags.CheckInIdentitySelfHosted);

        var harness = new VerificationEndpointHarness(new IdentityMetricsWorld().OrgWide, [.. flags]);

        var answer = await harness.Controller.GetMetrics(null, null, harness.Flags, harness.Identity.Object);

        var dto = Assert.IsType<VerificationMetricsDto>(Assert.IsType<OkObjectResult>(answer.Result).Value);
        Assert.Null(dto.Identity);
        harness.Identity.VerifyNoOtherCalls();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));
        Assert.Equal(PreviousMembers.Order(), Members(json.RootElement).Order());
        Assert.Equal((7, 3, 2, 4, 1, 5, 0.4), (dto.Ok, dto.Review, dto.NotOk, dto.OperatorApproved, dto.OperatorRejected, dto.OperatorDecisions, dto.OperatorShare));
    }

    [Fact]
    public async Task GetMetrics_WithBothFlagsOn_AddsTheCountersOfTheSameScopeAndPeriod()
    {
        var world = new IdentityMetricsWorld();
        var harness = new VerificationEndpointHarness(world.Restricted, FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted);
        harness.Identity
            .Setup(metrics => metrics.GetAsync(It.IsAny<HostScope>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample());

        var answer = await harness.Controller.GetMetrics(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), harness.Flags, harness.Identity.Object);

        var dto = Assert.IsType<VerificationMetricsDto>(Assert.IsType<OkObjectResult>(answer.Result).Value);
        var fromUtc = RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 1));
        var toUtc = RomeCalendar.StartOfDayUtc(new DateOnly(2026, 12, 1));
        harness.Queue.Verify(queue => queue.GetMetricsAsync(world.Restricted, fromUtc, toUtc, It.IsAny<CancellationToken>()), Times.Once);
        harness.Identity.Verify(metrics => metrics.GetAsync(world.Restricted, fromUtc, toUtc, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), 7, 3, 2), (dto.From, dto.To, dto.Ok, dto.Review, dto.NotOk));
        Assert.Equal(500, dto.Identity!.Opened);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));
        Assert.Equal(PreviousMembers.Append("identity").Order(), Members(json.RootElement).Order());
    }

    [Fact]
    public void IdentityMetricsDto_IsTheContractWithTheFrontend()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(IdentityVerificationMetricsDto.From(Sample()), Web));
        var identity = json.RootElement;

        Assert.Equal(
            new[] { "opened", "outcomes", "automationRate", "shadowApprovalRate", "shadowVsHuman", "latencyMs", "reviewSeconds", "coverage" }.Order(),
            Members(identity).Order());
        Assert.Equal(500, identity.GetProperty("opened").GetInt32());
        Assert.Equal(
            new[] { "autoApproved", "review", "assisted", "consentRefused", "formatUnsupported", "technicalError", "unavailable", "minorExcluded", "abandoned", "inProgress" }.Order(),
            Members(identity.GetProperty("outcomes")).Order());
        Assert.Equal(425, identity.GetProperty("outcomes").GetProperty("autoApproved").GetInt32());
        Assert.Equal(3, identity.GetProperty("outcomes").GetProperty("inProgress").GetInt32());

        foreach (var rate in new[] { "automationRate", "shadowApprovalRate" })
            Assert.Equal(new[] { "value", "wilsonLow", "wilsonHigh", "n" }.Order(), Members(identity.GetProperty(rate)).Order());

        Assert.Equal(0.85, identity.GetProperty("automationRate").GetProperty("value").GetDouble());
        Assert.Equal(0.8160389193683016, identity.GetProperty("automationRate").GetProperty("wilsonLow").GetDouble(), 9);
        Assert.Equal(500, identity.GetProperty("automationRate").GetProperty("n").GetInt32());
        Assert.Equal(
            new[] { "approveApprove", "approveReject", "reviewApprove", "reviewReject" }.Order(),
            Members(identity.GetProperty("shadowVsHuman")).Order());
        Assert.Equal(2, identity.GetProperty("shadowVsHuman").GetProperty("approveReject").GetInt32());

        foreach (var times in new[] { "latencyMs", "reviewSeconds" })
            Assert.Equal(new[] { "p50", "p95", "n" }.Order(), Members(identity.GetProperty(times)).Order());

        Assert.Equal(850, identity.GetProperty("latencyMs").GetProperty("p50").GetDouble());
        Assert.Equal(7200, identity.GetProperty("reviewSeconds").GetProperty("p95").GetDouble());
        var cell = Assert.Single(identity.GetProperty("coverage").EnumerateArray());
        Assert.Equal(
            new[] { "formatId", "channel", "opened", "autoApproved", "shadowApproved", "rate", "wilsonLow", "wilsonHigh" }.Order(),
            Members(cell).Order());
        Assert.Equal("it-cie-3.0", cell.GetProperty("formatId").GetString());
        Assert.Equal("web:ios-safari", cell.GetProperty("channel").GetString());
        Assert.Equal(0.9, cell.GetProperty("rate").GetDouble());
    }

    [Fact]
    public void IdentityMetricsDto_NothingMeasured_IsNullAndNotZero()
    {
        var none = new IdentityVerificationMetrics(
            0,
            IdentityOutcomeCounts.From([]),
            IdentityRate.Of(0, 0),
            IdentityRate.Of(0, 0),
            new IdentityShadowVsHuman(0, 0, 0, 0),
            IdentityPercentiles.Of([]),
            IdentityPercentiles.Of([]),
            []);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(IdentityVerificationMetricsDto.From(none), Web));
        var rate = json.RootElement.GetProperty("automationRate");

        Assert.Equal(JsonValueKind.Null, rate.GetProperty("value").ValueKind);
        Assert.Equal((0.0, 1.0, 0), (rate.GetProperty("wilsonLow").GetDouble(), rate.GetProperty("wilsonHigh").GetDouble(), rate.GetProperty("n").GetInt32()));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("latencyMs").GetProperty("p50").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("reviewSeconds").GetProperty("p95").ValueKind);
        Assert.Equal(0, json.RootElement.GetProperty("coverage").GetArrayLength());
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("2025-11-27", "2026-11-27", true)]
    [InlineData("2025-11-26", "2026-11-27", false)]
    [InlineData("2026-11-28", "2026-11-27", false)]
    public async Task GetMetrics_ThePeriodRule_IsTheOneOfTheMetrics(string? from, string? to, bool allowed)
    {
        var harness = new VerificationEndpointHarness(new IdentityMetricsWorld().OrgWide, FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted);
        harness.Identity
            .Setup(metrics => metrics.GetAsync(It.IsAny<HostScope>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample());

        var answer = await harness.Controller.GetMetrics(
            from is null ? null : DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture),
            harness.Flags,
            harness.Identity.Object);

        if (allowed)
        {
            var dto = Assert.IsType<VerificationMetricsDto>(Assert.IsType<OkObjectResult>(answer.Result).Value);
            Assert.Equal(from is null ? new DateOnly(2026, 10, 29) : DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), dto.From);
            Assert.Equal(new DateOnly(2026, 11, 27), dto.To);
            return;
        }

        var problem = Assert.IsType<ObjectResult>(answer.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("verification_metrics_period_invalid", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
        harness.Queue.VerifyNoOtherCalls();
        harness.Identity.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetMetrics_WithoutAScope_Is401_AndNothingIsRead()
    {
        var harness = new VerificationEndpointHarness(null, FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted);

        var answer = await harness.Controller.GetMetrics(null, null, harness.Flags, harness.Identity.Object);

        Assert.IsType<UnauthorizedResult>(answer.Result);
        harness.Queue.VerifyNoOtherCalls();
        harness.Identity.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetMetrics_KeepsItsRoute_AndTheOperatorPolicy()
    {
        var method = typeof(VerificationController).GetMethod(nameof(VerificationController.GetMetrics))!;

        Assert.Equal("metrics", Assert.Single(method.GetCustomAttributes<HttpGetAttribute>()).Template);
        Assert.Equal([CasazenPolicies.VerificationDecision], typeof(VerificationController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(attribute => attribute.Policy));
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.DoesNotContain(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true), attribute => attribute.Policy != CasazenPolicies.VerificationDecision);
        Assert.Empty(method.GetCustomAttributes<FeatureGateAttribute>(inherit: true));
    }
}
