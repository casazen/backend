using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Identity.Statistics;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public sealed class IdentityVerificationMetricsServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static IdentityVerificationMetricsService Service(AppDbContext db, IdentityVerificationOptions? options = null, DateTimeOffset? now = null) =>
        new(db, new FakeTimeProvider(now ?? IdentityMetricsWorld.Now), Options.Create(options ?? new IdentityVerificationOptions()));

    private static async Task<IdentityVerificationMetrics> MetricsAsync(IdentityMetricsWorld world, IdentityVerificationOptions? options = null, DateTimeOffset? now = null)
    {
        await world.SaveAsync();
        await using var db = world.NewDb();
        return await Service(db, options, now).GetAsync(world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct);
    }

    /// <summary>
    /// Proof (data): the denominator is complete. Every category has its own count, a closed verification with no code or an unknown one is a person reviewing, a step still open
    /// is abandoned or in progress by its age, and the ten categories always add up to the steps opened in the period; a step opened outside the period, and a session that never
    /// opened the identity step, are not counted.
    /// </summary>
    [Fact]
    public async Task IdentityVerificationMetrics_CategoriesSumToOpened()
    {
        var world = new IdentityMetricsWorld();
        void Closed(string? code, int count = 1)
        {
            for (var i = 0; i < count; i++)
                world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 30), code);
        }

        Closed(IdentityOutcomeCodes.AutoApproved, 3);
        Closed(IdentityOutcomeCodes.Review);
        Closed("a_code_of_the_future");
        Closed(null);
        Closed(IdentityOutcomeCodes.Assisted);
        Closed(IdentityOutcomeCodes.ConsentRefused);
        Closed(IdentityOutcomeCodes.FormatUnsupported);
        Closed(IdentityOutcomeCodes.TechnicalError);
        Closed(IdentityOutcomeCodes.Unavailable);
        Closed(IdentityOutcomeCodes.MinorExcluded);
        Closed(IdentityOutcomeCodes.Abandoned);
        world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.Now.UtcDateTime.AddDays(-2));
        world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.Now.UtcDateTime.AddHours(-1));

        world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.From.AddSeconds(-1), IdentityMetricsWorld.From.AddMinutes(5), IdentityOutcomeCodes.AutoApproved);
        world.AddSession(world.AddStay(world.GrantedPropertyId), IdentityMetricsWorld.To, IdentityMetricsWorld.To.AddMinutes(5), IdentityOutcomeCodes.AutoApproved);
        world.AddSession(world.AddStay(world.GrantedPropertyId), openedAt: null);

        var metrics = await MetricsAsync(world);

        Assert.Equal(15, metrics.Opened);
        Assert.Equal(metrics.Opened, metrics.Outcomes.Total);
        Assert.Equal(
            (AutoApproved: 3, Review: 3, Assisted: 1, ConsentRefused: 1, FormatUnsupported: 1, TechnicalError: 1, Unavailable: 1, MinorExcluded: 1, Abandoned: 2, InProgress: 1),
            (metrics.Outcomes.AutoApproved, metrics.Outcomes.Review, metrics.Outcomes.Assisted, metrics.Outcomes.ConsentRefused, metrics.Outcomes.FormatUnsupported,
                metrics.Outcomes.TechnicalError, metrics.Outcomes.Unavailable, metrics.Outcomes.MinorExcluded, metrics.Outcomes.Abandoned, metrics.Outcomes.InProgress));
        Assert.Equal(metrics.Opened, IdentityOutcomeCategories.All.Sum(category => metrics.Outcomes.Of(category)));
        Assert.Equal(metrics.Opened, metrics.AutomationRate.N);
        Assert.Equal(metrics.Opened, metrics.Coverage.Sum(cell => cell.Opened));
    }

    [Fact]
    public void IdentityOutcomeCategories_EveryTerminalCodeHasItsCategory_AndTheCodesComeBack()
    {
        Assert.Equal(IdentityOutcomeCodes.All.Count + 1, IdentityOutcomeCategories.All.Count);
        foreach (var code in IdentityOutcomeCodes.All)
            Assert.Equal(code, IdentityOutcomeCategories.CodeOf(IdentityOutcomeCategories.OfTerminalCode(code)));

        Assert.Equal("in_progress", IdentityOutcomeCategories.CodeOf(IdentityOutcomeCategory.InProgress));
        Assert.Equal(IdentityOutcomeCategories.All.Count, IdentityOutcomeCategories.All.Select(IdentityOutcomeCategories.CodeOf).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(IdentityOutcomeCategory.Review, IdentityOutcomeCategories.OfTerminalCode(null));
        Assert.Equal(IdentityOutcomeCategory.Review, IdentityOutcomeCategories.OfTerminalCode("whatever"));
    }

    [Fact]
    public async Task IdentityVerificationMetrics_AnOpenStepIsAbandonedOnlyOnceOlderThanTheRetention()
    {
        var now = IdentityMetricsWorld.Now.UtcDateTime;
        var world = new IdentityMetricsWorld();
        world.AddSession(world.AddStay(world.GrantedPropertyId), now.AddHours(-23).AddMinutes(-59));
        world.AddSession(world.AddStay(world.GrantedPropertyId), now.AddHours(-24));
        world.AddSession(world.AddStay(world.GrantedPropertyId), now.AddHours(-24).AddSeconds(-1));
        world.AddSession(world.AddStay(world.GrantedPropertyId), now.AddHours(-30));
        world.AddSession(world.AddStay(world.GrantedPropertyId), now.AddHours(-49));
        await world.SaveAsync();

        async Task<(int Abandoned, int InProgress)> CountAsync(int hours)
        {
            await using var db = world.NewDb();
            var options = new IdentityVerificationOptions { Retention = new IdentityRetentionOptions { AbandonedAfterHours = hours } };
            var metrics = await Service(db, options).GetAsync(world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct);
            Assert.Equal(5, metrics.Outcomes.Total);
            return (metrics.Outcomes.Abandoned, metrics.Outcomes.InProgress);
        }

        Assert.Equal((3, 2), await CountAsync(24));
        Assert.Equal((1, 4), await CountAsync(48));
        Assert.Equal((5, 0), await CountAsync(1));

        Assert.Equal((3, 2), await CountAsync(0));
        Assert.Equal((3, 2), await CountAsync(-5));
        Assert.Equal(24, new IdentityRetentionOptions().AbandonedAfterHours);
    }

    [Fact]
    public async Task IdentityVerificationMetrics_TheRatesCarryTheWilsonIntervalOfTheStatistics()
    {
        var world = new IdentityMetricsWorld();
        for (var i = 0; i < 425; i++)
        {
            var stay = world.AddStay(world.GrantedPropertyId);
            world.AddSession(stay, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.AutoApproved);
            world.AddDecision(stay, IdentityMetricsWorld.At(10, 10, 5), BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic);
        }

        for (var i = 0; i < 75; i++)
        {
            var stay = world.AddStay(world.GrantedPropertyId);
            world.AddSession(stay, IdentityMetricsWorld.At(11), IdentityMetricsWorld.At(11, 10, 5), IdentityOutcomeCodes.Review);
            world.AddDecision(stay, IdentityMetricsWorld.At(11, 10, 5), BiometricOutcome.Escalate, i < 50 ? BiometricOutcome.Accept : BiometricOutcome.Escalate);
        }

        var metrics = await MetricsAsync(world);

        Assert.Equal(500, metrics.Opened);
        Assert.Equal((425, 75), (metrics.Outcomes.AutoApproved, metrics.Outcomes.Review));
        Assert.Equal(500, metrics.AutomationRate.N);
        Assert.Equal(0.85, metrics.AutomationRate.Value);
        Assert.Equal(0.8160389193683016, metrics.AutomationRate.WilsonLow, 9);
        Assert.Equal(0.8786240423086118, metrics.AutomationRate.WilsonHigh, 9);
        Assert.Equal(IdentityStatistics.Wilson(425, 500), (metrics.AutomationRate.WilsonLow, metrics.AutomationRate.WilsonHigh));

        Assert.Equal(500, metrics.ShadowApprovalRate.N);
        Assert.Equal(0.95, metrics.ShadowApprovalRate.Value);
        Assert.Equal(IdentityStatistics.Wilson(475, 500), (metrics.ShadowApprovalRate.WilsonLow, metrics.ShadowApprovalRate.WilsonHigh));

        Assert.Equal(new IdentityShadowVsHuman(0, 0, 0, 0), metrics.ShadowVsHuman);
        Assert.Equal(500, metrics.LatencyMs.N);
        Assert.Equal(0, metrics.ReviewSeconds.N);
    }

    [Fact]
    public async Task IdentityVerificationMetrics_EachShadowDecisionIsPairedWithTheNextDecisionOfAPerson()
    {
        var world = new IdentityMetricsWorld();
        var open = IdentityMetricsWorld.At(10);
        var closed = IdentityMetricsWorld.At(10, 10, 5);

        Guid Reviewed(BiometricOutcome shadow, BiometricOutcome? person, int afterSeconds, BiometricOutcome outcome = BiometricOutcome.Escalate)
        {
            var stay = world.AddStay(world.GrantedPropertyId);
            world.AddSession(stay, open, closed, IdentityOutcomeCodes.Review);
            world.AddDecision(stay, closed, outcome, shadow);
            if (person is { } decided)
                world.AddOperator(stay, closed.AddSeconds(afterSeconds), decided);

            return stay;
        }

        Reviewed(BiometricOutcome.Accept, BiometricOutcome.Accept, 1800);
        Reviewed(BiometricOutcome.Accept, BiometricOutcome.Reject, 600);
        Reviewed(BiometricOutcome.Escalate, BiometricOutcome.Accept, 120);
        Reviewed(BiometricOutcome.Escalate, BiometricOutcome.Reject, 60);

        Reviewed(BiometricOutcome.Accept, null, 0);
        Reviewed(BiometricOutcome.Accept, BiometricOutcome.Escalate, 300);

        var earlier = Reviewed(BiometricOutcome.Accept, null, 0);
        world.AddOperator(earlier, open.AddMinutes(-60), BiometricOutcome.Accept);

        var legacy = world.AddStay(world.GrantedPropertyId);
        world.AddSession(legacy, open, closed, IdentityOutcomeCodes.ConsentRefused);
        world.AddLegacy(legacy, closed.AddMinutes(-30), BiometricOutcome.Accept);
        world.AddOperator(legacy, closed.AddMinutes(10), BiometricOutcome.Accept);

        var automatic = world.AddStay(world.GrantedPropertyId);
        world.AddSession(automatic, open, closed, IdentityOutcomeCodes.AutoApproved);
        world.AddDecision(automatic, closed, BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic);

        var twice = world.AddStay(world.GrantedPropertyId);
        world.AddSession(twice, open, closed, IdentityOutcomeCodes.Review);
        world.AddDecision(twice, closed, BiometricOutcome.Escalate, BiometricOutcome.Accept);
        world.AddOperator(twice, closed.AddMinutes(15), BiometricOutcome.Escalate);
        var secondOpen = open.AddMinutes(30);
        var secondClosed = secondOpen.AddMinutes(5);
        world.AddSession(twice, secondOpen, secondClosed, IdentityOutcomeCodes.Review);
        world.AddDecision(twice, secondClosed, BiometricOutcome.Escalate, BiometricOutcome.Escalate);
        world.AddOperator(twice, secondClosed.AddSeconds(3600), BiometricOutcome.Reject);

        var unanswered = world.AddStay(world.GrantedPropertyId);
        world.AddSession(unanswered, open, closed, IdentityOutcomeCodes.Review);
        world.AddDecision(unanswered, closed, BiometricOutcome.Escalate, BiometricOutcome.Accept);
        world.AddSession(unanswered, secondOpen, secondClosed, IdentityOutcomeCodes.Review);
        world.AddDecision(unanswered, secondClosed, BiometricOutcome.Escalate, BiometricOutcome.Accept);
        world.AddOperator(unanswered, secondClosed.AddSeconds(3300), BiometricOutcome.Accept);

        var late = world.AddStay(world.GrantedPropertyId);
        world.AddSession(late, IdentityMetricsWorld.To.AddMinutes(-10), IdentityMetricsWorld.To.AddMinutes(5), IdentityOutcomeCodes.Review);
        world.AddDecision(late, IdentityMetricsWorld.To.AddMinutes(5), BiometricOutcome.Escalate, BiometricOutcome.Escalate);
        world.AddOperator(late, IdentityMetricsWorld.To.AddDays(1), BiometricOutcome.Accept);

        var metrics = await MetricsAsync(world);

        Assert.Equal(14, metrics.Opened);
        Assert.Equal(new IdentityShadowVsHuman(ApproveApprove: 2, ApproveReject: 1, ReviewApprove: 2, ReviewReject: 2), metrics.ShadowVsHuman);

        Assert.Equal(14, metrics.ShadowApprovalRate.N);
        Assert.Equal(9, metrics.Coverage.Sum(cell => cell.ShadowApproved));
        Assert.Equal(9d / 14, metrics.ShadowApprovalRate.Value);

        Assert.Equal(7, metrics.ReviewSeconds.N);
        Assert.Equal(1800, metrics.ReviewSeconds.P50);
        Assert.Equal(IdentityMetricsWorld.To.AddDays(1).Subtract(IdentityMetricsWorld.To.AddMinutes(5)).TotalSeconds, metrics.ReviewSeconds.P95);

        Assert.Equal(13, metrics.LatencyMs.N);
        Assert.Equal(900, metrics.LatencyMs.P50);
    }

    /// <summary>
    /// Only the closure of the presence step writes a row of the policy, so only a session closed with its code (<c>auto_approved</c>, <c>review</c>, <c>technical_error</c>,
    /// <c>unavailable</c>) looks for one: a session closed with any other code (the guest asked for a person or refused the consent, a format the registry does not hold, a minor, an
    /// abandonment) never takes a row, even when the time between its opening and its closing holds one of its booking, and neither does a code that is missing or unknown.
    /// </summary>
    [Theory]
    [InlineData(IdentityOutcomeCodes.AutoApproved, true)]
    [InlineData(IdentityOutcomeCodes.Review, true)]
    [InlineData(IdentityOutcomeCodes.TechnicalError, true)]
    [InlineData(IdentityOutcomeCodes.Unavailable, true)]
    [InlineData(IdentityOutcomeCodes.Assisted, false)]
    [InlineData(IdentityOutcomeCodes.ConsentRefused, false)]
    [InlineData(IdentityOutcomeCodes.FormatUnsupported, false)]
    [InlineData(IdentityOutcomeCodes.MinorExcluded, false)]
    [InlineData(IdentityOutcomeCodes.Abandoned, false)]
    [InlineData("a_code_of_the_future", false)]
    [InlineData(null, false)]
    public async Task IdentityVerificationLedger_OnlyTheCodesOfTheClosureOfThePresenceStepLookForARow(string? code, bool takesTheRow)
    {
        var world = new IdentityMetricsWorld();
        var stay = world.AddStay(world.GrantedPropertyId);
        world.AddSession(stay, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 30), code);
        var decision = world.AddDecision(stay, IdentityMetricsWorld.At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Accept);
        await world.SaveAsync();
        await using var db = world.NewDb();

        var records = await IdentityVerificationLedger.ReadAsync(
            db, world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, IdentityMetricsWorld.Now.UtcDateTime, TimeSpan.FromHours(24), withReasons: false, Ct);

        Guid? expected = takesTheRow ? decision.Id : null;
        Assert.Equal(expected, Assert.Single(records).Decided?.Id);
    }

    [Fact]
    public async Task IdentityVerificationMetrics_AnAbandonedSessionClosedAfterALaterDecision_NeverTakesItsRow()
    {
        var world = new IdentityMetricsWorld();
        var (abandoned, later, decision) = world.AddAbandonedBeforeALaterDecision(world.GrantedPropertyId);
        await world.SaveAsync();
        await using var db = world.NewDb();

        var records = await IdentityVerificationLedger.ReadAsync(
            db, world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, IdentityMetricsWorld.Now.UtcDateTime, TimeSpan.FromHours(24), withReasons: false, Ct);

        var first = Assert.Single(records, record => record.SessionId == abandoned.Id);
        Assert.Equal(IdentityOutcomeCategory.Abandoned, first.Outcome);
        Assert.Null(first.Decided);
        Assert.Null(first.Human);
        var second = Assert.Single(records, record => record.SessionId == later.Id);
        Assert.Equal(decision.Id, second.Decided?.Id);
        Assert.True(second.Decided!.ShadowApproved);
        Assert.Equal(1100, second.Decided.DurationMs);
        Assert.True(second.Human!.Approved);
        Assert.Equal(1800, second.Human.ReviewSeconds);

        var metrics = IdentityVerificationMetricsService.Summarize(records);

        Assert.Equal(2, metrics.Opened);
        Assert.Equal((Abandoned: 1, Review: 1), (metrics.Outcomes.Abandoned, metrics.Outcomes.Review));
        Assert.Equal(new IdentityShadowVsHuman(1, 0, 0, 0), metrics.ShadowVsHuman);
        Assert.Equal(new IdentityPercentiles(1100, 1100, 1), metrics.LatencyMs);
        Assert.Equal(new IdentityPercentiles(1800, 1800, 1), metrics.ReviewSeconds);
        Assert.Equal(
            [
                ("it-cie-3.0", IdentityChannels.AndroidChrome, 1, 0, 1),
                (IdentityCoverageCell.UnknownFormat, IdentityChannels.IosSafari, 1, 0, 0),
            ],
            metrics.Coverage.Select(cell => (cell.FormatId, cell.Channel, cell.Opened, cell.AutoApproved, cell.ShadowApproved)));
    }

    [Fact]
    public async Task IdentityVerificationMetrics_LatencyAndReviewTimeAreNearestRankPercentiles()
    {
        var world = new IdentityMetricsWorld();
        for (var k = 1; k <= 20; k++)
        {
            var stay = world.AddStay(world.GrantedPropertyId);
            var closed = IdentityMetricsWorld.At(12, 9, 0, 0).AddMinutes(k);
            world.AddSession(stay, closed.AddMinutes(-1), closed, IdentityOutcomeCodes.Review);
            world.AddDecision(stay, closed, BiometricOutcome.Escalate, BiometricOutcome.Accept, durationMs: 100 * k);
            world.AddOperator(stay, closed.AddSeconds(60 * k), BiometricOutcome.Accept);
        }

        var metrics = await MetricsAsync(world);

        Assert.Equal(new IdentityPercentiles(1000, 1900, 20), metrics.LatencyMs);
        Assert.Equal(new IdentityPercentiles(600, 1140, 20), metrics.ReviewSeconds);

        var values = Enumerable.Range(1, 20).Select(k => 100d * k).ToList();
        Assert.Equal((IdentityStatistics.Percentile(values, 50), IdentityStatistics.Percentile(values, 95)), (metrics.LatencyMs.P50, metrics.LatencyMs.P95));
    }

    [Fact]
    public async Task IdentityVerificationMetrics_NoStepOpened_IsNoMeasurement()
    {
        var world = new IdentityMetricsWorld();

        var metrics = await MetricsAsync(world);

        Assert.Equal(0, metrics.Opened);
        Assert.Equal(0, metrics.Outcomes.Total);
        Assert.Equal(new IdentityRate(null, 0.0, 1.0, 0), metrics.AutomationRate);
        Assert.Equal(new IdentityRate(null, 0.0, 1.0, 0), metrics.ShadowApprovalRate);
        Assert.Equal(new IdentityShadowVsHuman(0, 0, 0, 0), metrics.ShadowVsHuman);
        Assert.Equal(new IdentityPercentiles(null, null, 0), metrics.LatencyMs);
        Assert.Equal(new IdentityPercentiles(null, null, 0), metrics.ReviewSeconds);
        Assert.Empty(metrics.Coverage);
    }

    [Fact]
    public async Task IdentityVerificationMetrics_CoverageIsGroupedByFormatAndChannel()
    {
        var world = new IdentityMetricsWorld();
        var open = IdentityMetricsWorld.At(10);
        var closed = IdentityMetricsWorld.At(10, 10, 5);

        void Step(string? format, string? channel, string? code, BiometricOutcome? shadow)
        {
            var stay = world.AddStay(world.GrantedPropertyId);
            world.AddSession(stay, open, code is null ? null : closed, code, format, channel);
            if (shadow is { } would)
            {
                world.AddDecision(
                    stay,
                    closed,
                    code == IdentityOutcomeCodes.AutoApproved ? BiometricOutcome.Accept : BiometricOutcome.Escalate,
                    would,
                    code == IdentityOutcomeCodes.AutoApproved ? IdentityDecisionMode.Automatic : IdentityDecisionMode.Observation,
                    formatId: format,
                    channel: channel);
            }
        }

        for (var i = 0; i < 3; i++)
            Step("it-cie-3.0", IdentityChannels.IosSafari, IdentityOutcomeCodes.AutoApproved, BiometricOutcome.Accept);

        Step("it-cie-3.0", IdentityChannels.IosSafari, IdentityOutcomeCodes.Review, BiometricOutcome.Accept);
        Step("it-cie-3.0", IdentityChannels.AndroidChrome, IdentityOutcomeCodes.Review, BiometricOutcome.Escalate);
        Step("it-cie-3.0", IdentityChannels.AndroidChrome, null, null);
        Step(null, null, IdentityOutcomeCodes.ConsentRefused, null);
        Step("  ", IdentityChannels.Other, IdentityOutcomeCodes.FormatUnsupported, null);

        var metrics = await MetricsAsync(world);

        Assert.Equal(8, metrics.Opened);
        Assert.Equal(
            [
                ("it-cie-3.0", IdentityChannels.AndroidChrome, 2, 0, 0),
                ("it-cie-3.0", IdentityChannels.IosSafari, 4, 3, 4),
                (IdentityCoverageCell.UnknownFormat, IdentityChannels.Other, 2, 0, 0),
            ],
            metrics.Coverage.Select(cell => (cell.FormatId, cell.Channel, cell.Opened, cell.AutoApproved, cell.ShadowApproved)));
        Assert.Equal(metrics.Opened, metrics.Coverage.Sum(cell => cell.Opened));

        var cie = metrics.Coverage.Single(cell => cell is { FormatId: "it-cie-3.0", Channel: IdentityChannels.IosSafari });
        Assert.Equal(0.75, cie.Rate);
        Assert.Equal(IdentityStatistics.Wilson(3, 4), (cie.WilsonLow, cie.WilsonHigh));
        var none = metrics.Coverage.Single(cell => cell.AutoApproved == 0 && cell.Opened == 2 && cell.Channel == IdentityChannels.AndroidChrome);
        Assert.Equal(0.0, none.Rate);
        Assert.Equal(IdentityStatistics.Wilson(0, 2), (none.WilsonLow, none.WilsonHigh));
    }

    [Fact]
    public async Task IdentityVerificationLedger_ReadsTheReasonsOnlyWhenAskedFor()
    {
        var world = new IdentityMetricsWorld();
        var stay = world.AddStay(world.GrantedPropertyId);
        world.AddSession(stay, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);
        world.AddDecision(stay, IdentityMetricsWorld.At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Escalate, reasonsJson: "[{\"check\":\"similarity\",\"result\":\"failed\",\"value\":0.6123}]");
        await world.SaveAsync();
        await using var db = world.NewDb();

        var without = await IdentityVerificationLedger.ReadAsync(
            db, world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, IdentityMetricsWorld.Now.UtcDateTime, TimeSpan.FromHours(24), withReasons: false, Ct);
        var with = await IdentityVerificationLedger.ReadAsync(
            db, world.OrgWide, IdentityMetricsWorld.From, IdentityMetricsWorld.To, IdentityMetricsWorld.Now.UtcDateTime, TimeSpan.FromHours(24), withReasons: true, Ct);

        Assert.Null(Assert.Single(without).Decided!.ReasonsJson);
        Assert.Contains("similarity", Assert.Single(with).Decided!.ReasonsJson, StringComparison.Ordinal);
    }
}
