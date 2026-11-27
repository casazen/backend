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

/// <summary>
/// E73/F01/US03/T01 (NF-196, decision D10): the counters of the self-hosted identity verification. The denominator is every identity step opened in the period and the ten
/// categories add up to it (refusals, unsupported formats, errors, unavailability, minors, abandonments and verifications in progress included); an open step is abandoned only
/// once it is older than the retention says; the rates carry the Wilson 95% interval of the statistics; the shadow decision of the policy is paired with the first decision of a
/// person after it on the same booking (the row that asks for a new photo is not a decision, and no decision is paired twice); the latency and the review time are the
/// nearest-rank percentiles of the statistics. The scope (another org, a property outside the reach) is in <see cref="IdentityMetricsScopeTests"/>.
/// </summary>
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

        // Never counted: opened before the period, opened at its end (excluded), and a session that never opened the identity step (the legacy flow).
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

    /// <summary>The categories are the codes of the outcome of the session, plus the one an open step has: nothing is lost between the code a session keeps and the category the counters give it.</summary>
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

    /// <summary>
    /// A step that is still open is abandoned only when it is OLDER than <c>Retention:AbandonedAfterHours</c> (exactly that old is still in progress), the time is the one of the
    /// configuration, and a setting that is not a positive number of hours reads as the default of the section, never as «everything is abandoned».
    /// </summary>
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

        // Not a positive number of hours: the default of the section (24 hours), not an instant abandonment of every open step.
        Assert.Equal((3, 2), await CountAsync(0));
        Assert.Equal((3, 2), await CountAsync(-5));
        Assert.Equal(24, new IdentityRetentionOptions().AbandonedAfterHours);
    }

    /// <summary>
    /// AC2: the automation rate and the shadow approval rate are counts out of the opened steps with the Wilson 95% interval of <see cref="IdentityStatistics"/>: the case of the
    /// specification, 425 closed automatically out of 500, has the bounds 0.8160 and 0.8786, and the attempts the policy would have approved are counted from the log.
    /// </summary>
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

        // The attempts the policy would have approved: the 425 it approved and 50 of the reviews, out of the 500 opened.
        Assert.Equal(500, metrics.ShadowApprovalRate.N);
        Assert.Equal(0.95, metrics.ShadowApprovalRate.Value);
        Assert.Equal(IdentityStatistics.Wilson(475, 500), (metrics.ShadowApprovalRate.WilsonLow, metrics.ShadowApprovalRate.WilsonHigh));

        // No person decided yet: nothing to compare, and the times are of the 500 attempts.
        Assert.Equal(new IdentityShadowVsHuman(0, 0, 0, 0), metrics.ShadowVsHuman);
        Assert.Equal(500, metrics.LatencyMs.N);
        Assert.Equal(0, metrics.ReviewSeconds.N);
    }

    /// <summary>
    /// AC3: each shadow decision is paired with the FIRST decision of a person after it on the same booking and before the next decision of the policy: approve/approve,
    /// approve/reject (the critical cell), review/approve, review/reject. The row that asks the guest for a new photo is not a decision, a person's row from before the policy
    /// decided is not «later», a decision is paired once (not with the earlier review of a booking handed over twice), the rows of the legacy path are not decisions of the policy,
    /// and a step opened in the period is paired with decisions that came after its end.
    /// </summary>
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

        // The policy would approve and nobody decided yet; or the only row of the operator asks the guest for a new photo: no decision.
        Reviewed(BiometricOutcome.Accept, null, 0);
        Reviewed(BiometricOutcome.Accept, BiometricOutcome.Escalate, 300);

        // A person's row from before the policy decided is not a later decision.
        var earlier = Reviewed(BiometricOutcome.Accept, null, 0);
        world.AddOperator(earlier, open.AddMinutes(-60), BiometricOutcome.Accept);

        // The legacy path: its row is not a decision of the policy (no shadow outcome, not counted), and the session that closed without one has nothing to pair.
        var legacy = world.AddStay(world.GrantedPropertyId);
        world.AddSession(legacy, open, closed, IdentityOutcomeCodes.ConsentRefused);
        world.AddLegacy(legacy, closed.AddMinutes(-30), BiometricOutcome.Accept);
        world.AddOperator(legacy, closed.AddMinutes(10), BiometricOutcome.Accept);

        // The automatic approval: the policy decided, nobody else did.
        var automatic = world.AddStay(world.GrantedPropertyId);
        world.AddSession(automatic, open, closed, IdentityOutcomeCodes.AutoApproved);
        world.AddDecision(automatic, closed, BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic);

        // A booking handed over twice: the first review (shadow approve) was answered by a request for a new photo; the second (shadow review) was decided by a person. The decision
        // is of the second review only, and the review time of the second counts from its own escalation.
        var twice = world.AddStay(world.GrantedPropertyId);
        world.AddSession(twice, open, closed, IdentityOutcomeCodes.Review);
        world.AddDecision(twice, closed, BiometricOutcome.Escalate, BiometricOutcome.Accept);
        world.AddOperator(twice, closed.AddMinutes(15), BiometricOutcome.Escalate);
        var secondOpen = open.AddMinutes(30);
        var secondClosed = secondOpen.AddMinutes(5);
        world.AddSession(twice, secondOpen, secondClosed, IdentityOutcomeCodes.Review);
        world.AddDecision(twice, secondClosed, BiometricOutcome.Escalate, BiometricOutcome.Escalate);
        world.AddOperator(twice, secondClosed.AddSeconds(3600), BiometricOutcome.Reject);

        // Two reviews and one decision of a person: the decision is of the later review, never of both (the first has none before the next decision of the policy).
        var unanswered = world.AddStay(world.GrantedPropertyId);
        world.AddSession(unanswered, open, closed, IdentityOutcomeCodes.Review);
        world.AddDecision(unanswered, closed, BiometricOutcome.Escalate, BiometricOutcome.Accept);
        world.AddSession(unanswered, secondOpen, secondClosed, IdentityOutcomeCodes.Review);
        world.AddDecision(unanswered, secondClosed, BiometricOutcome.Escalate, BiometricOutcome.Accept);
        world.AddOperator(unanswered, secondClosed.AddSeconds(3300), BiometricOutcome.Accept);

        // A step opened at the end of the period and decided after it: paired all the same.
        var late = world.AddStay(world.GrantedPropertyId);
        world.AddSession(late, IdentityMetricsWorld.To.AddMinutes(-10), IdentityMetricsWorld.To.AddMinutes(5), IdentityOutcomeCodes.Review);
        world.AddDecision(late, IdentityMetricsWorld.To.AddMinutes(5), BiometricOutcome.Escalate, BiometricOutcome.Escalate);
        world.AddOperator(late, IdentityMetricsWorld.To.AddDays(1), BiometricOutcome.Accept);

        var metrics = await MetricsAsync(world);

        Assert.Equal(14, metrics.Opened);
        // approve/approve: the first booking and the second review of the booking answered once; approve/reject: the second; review/approve: the third and the late one;
        // review/reject: the fourth and the second review of the booking handed over twice.
        Assert.Equal(new IdentityShadowVsHuman(ApproveApprove: 2, ApproveReject: 1, ReviewApprove: 2, ReviewReject: 2), metrics.ShadowVsHuman);

        // The shadow approvals are counted from the log of the policy: the legacy row and the sessions with no decision of the policy are not.
        Assert.Equal(14, metrics.ShadowApprovalRate.N);
        Assert.Equal(9, metrics.Coverage.Sum(cell => cell.ShadowApproved));
        Assert.Equal(9d / 14, metrics.ShadowApprovalRate.Value);

        // The review time counts from the escalation to the decision: 1800, 600, 120 and 60 s; 3600 s for the second review of the booking handed over twice; 3300 s for the
        // second review of the booking answered once; and 86400 - 300 s for the step decided the day after the period ended. Seven reviews: p50 is the 4th, p95 the 7th.
        Assert.Equal(7, metrics.ReviewSeconds.N);
        Assert.Equal(1800, metrics.ReviewSeconds.P50);
        Assert.Equal(IdentityMetricsWorld.To.AddDays(1).Subtract(IdentityMetricsWorld.To.AddMinutes(5)).TotalSeconds, metrics.ReviewSeconds.P95);

        // The legacy row (777 ms) is not a presence step of the policy: only the 13 rows of the policy are timed.
        Assert.Equal(13, metrics.LatencyMs.N);
        Assert.Equal(900, metrics.LatencyMs.P50);
    }

    /// <summary>AC4: the latency and the review time are the nearest-rank percentiles of the statistics (rank <c>ceil(p * n)</c>), over the rows of the policy and over the reviews a person answered.</summary>
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

        // 20 values: p50 is the 10th (1000 ms, 600 s), p95 the 19th (1900 ms, 1140 s).
        Assert.Equal(new IdentityPercentiles(1000, 1900, 20), metrics.LatencyMs);
        Assert.Equal(new IdentityPercentiles(600, 1140, 20), metrics.ReviewSeconds);

        // The same function the report of the qualification uses.
        var values = Enumerable.Range(1, 20).Select(k => 100d * k).ToList();
        Assert.Equal((IdentityStatistics.Percentile(values, 50), IdentityStatistics.Percentile(values, 95)), (metrics.LatencyMs.P50, metrics.LatencyMs.P95));
    }

    /// <summary>No step opened is no measurement: zeros, no rate (not a rate of 0), the interval (0, 1), no percentile and no cell.</summary>
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

    /// <summary>
    /// AC5 (coverage): the steps are grouped by format and by channel (<c>unknown</c> for a document that was not read as a format, <c>web:other</c> for a channel that is missing),
    /// in ordinal order, each cell with its opened steps, automatic approvals, shadow approvals and the automation rate with its Wilson interval; the cells add up to the opened steps.
    /// </summary>
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

    /// <summary>
    /// Privacy: the counters never read the reasons of a decision (they carry the numbers a person was compared with); only the export of the pilot asks for them, and then writes
    /// their codes. The one reader of both says so.
    /// </summary>
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
