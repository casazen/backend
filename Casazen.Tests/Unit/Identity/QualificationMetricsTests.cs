using Casazen.Core.Identity.Statistics;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Qualification;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F01/US02/T01 (NF-198, D12): the pure parts of the qualification runner: the opt-in of the long runs, the aggregate metrics at the thresholds of the policy, the aggregation of the scores of the
/// frames, the comparison of the reference model with a candidate at the same acceptances, the sample of the impostor trials and the normalization the exactness of a field is judged by. The real
/// pipeline over generated pictures is <c>QualificationSmokeTests</c>.
/// </summary>
public sealed class QualificationMetricsTests
{
    // ───── the opt-in of the long runs ─────

    private static Func<string, string?> Variables(params (string Variable, string Value)[] set)
    {
        var values = set.ToDictionary(entry => entry.Variable, entry => entry.Value, StringComparer.Ordinal);
        return variable => values.GetValueOrDefault(variable);
    }

    [Fact]
    public void SkipReason_WithoutTheManifestOrTheOutputFolder_SaysWhichVariableIsMissing()
    {
        string[] runner = [QualificationSplits.Development, QualificationSplits.Calibration];

        var none = QualificationEnvironment.SkipReason(Variables(), runner, freeze: false);
        var noOut = QualificationEnvironment.SkipReason(Variables((QualificationEnvironment.ManifestVariable, "m.json"), (QualificationEnvironment.SplitVariable, "development")), runner, freeze: false);

        Assert.Contains(QualificationEnvironment.ManifestVariable, none, StringComparison.Ordinal);
        Assert.Contains(QualificationEnvironment.OutVariable, noOut, StringComparison.Ordinal);
        Assert.Contains("opt-in", none, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipReason_ARunServesTheSplitsItNames_AndOnlyThose()
    {
        string[] runner = [QualificationSplits.Development, QualificationSplits.Calibration];
        string[] final = [QualificationSplits.Test];
        Func<string, string?> Set(string split) => Variables(
            (QualificationEnvironment.ManifestVariable, "m.json"), (QualificationEnvironment.OutVariable, "out"), (QualificationEnvironment.SplitVariable, split));

        Assert.Null(QualificationEnvironment.SkipReason(Set("development"), runner, freeze: false));
        Assert.Null(QualificationEnvironment.SkipReason(Set(" calibration "), runner, freeze: false));
        Assert.Null(QualificationEnvironment.SkipReason(Set("test"), final, freeze: false));

        // The test split belongs to the final test, and the others to the runner: neither takes the other's.
        Assert.Contains("does not serve", QualificationEnvironment.SkipReason(Set("test"), runner, freeze: false), StringComparison.Ordinal);
        Assert.Contains("does not serve", QualificationEnvironment.SkipReason(Set("development"), final, freeze: false), StringComparison.Ordinal);
        Assert.Contains(QualificationEnvironment.SplitVariable, QualificationEnvironment.SkipReason(Variables((QualificationEnvironment.ManifestVariable, "m.json"), (QualificationEnvironment.OutVariable, "out")), runner, freeze: false), StringComparison.Ordinal);
    }

    [Fact]
    public void SkipReason_TheFreezeIsAnExplicitAct_AndWhileItIsAskedForTheFinalTestDoesNotRun()
    {
        string[] final = [QualificationSplits.Test];
        var set = new[] { (QualificationEnvironment.ManifestVariable, "m.json"), (QualificationEnvironment.OutVariable, "out"), (QualificationEnvironment.SplitVariable, "test") };
        var freezing = set.Append((QualificationEnvironment.FreezeVariable, "1")).ToArray();

        // The freeze runs only when it is asked for, and takes no split; the final test does not run on the same request, so no run writes the lock it is about to be checked against.
        Assert.Contains(QualificationEnvironment.FreezeVariable, QualificationEnvironment.SkipReason(Variables(set), [], freeze: true), StringComparison.Ordinal);
        Assert.Null(QualificationEnvironment.SkipReason(Variables(freezing), [], freeze: true));
        Assert.Contains(QualificationEnvironment.FreezeVariable, QualificationEnvironment.SkipReason(Variables(freezing), final, freeze: false), StringComparison.Ordinal);
        Assert.Null(QualificationEnvironment.SkipReason(Variables(set), final, freeze: false));
        Assert.True(QualificationEnvironment.IsFreeze("TRUE"));
        Assert.False(QualificationEnvironment.IsFreeze("0"));
        Assert.False(QualificationEnvironment.IsFreeze(null));
    }

    [Fact]
    public void TheFactsOfTheLongRuns_AreSkippedHereWithTheirReason_BecauseCiSetsNoVariable()
    {
        // The environment of a test run is not the one of a qualification: with none of the variables set (as in CI) the attributes carry a reason to skip.
        if (Environment.GetEnvironmentVariable(QualificationEnvironment.ManifestVariable) is not null)
            return;

        Assert.NotNull(new QualificationFactAttribute(QualificationSplits.Development).Skip);
        Assert.NotNull(new QualificationFreezeFactAttribute().Skip);
    }

    // ───── aggregation, normalization, sample ─────

    [Fact]
    public void Aggregate_IsTheRuleOfThePolicy_OverTheScoresOfTheFrames()
    {
        Assert.Equal(0.7, QualificationMetrics.Aggregate(IdentityAggregationRules.Median, [0.9, 0.5, 0.7]));
        Assert.Equal(0.6, QualificationMetrics.Aggregate(IdentityAggregationRules.Median, [0.4, 0.8]), 12);
        Assert.Equal(0.5, QualificationMetrics.Aggregate(IdentityAggregationRules.Min, [0.9, 0.5, 0.7]));
        Assert.Equal(0.7, QualificationMetrics.Aggregate(IdentityAggregationRules.Mean, [0.9, 0.5, 0.7]), 12);
        Assert.Throws<ArgumentException>(() => QualificationMetrics.Aggregate("best", [0.9]));
        Assert.Throws<ArgumentException>(() => QualificationMetrics.Aggregate(IdentityAggregationRules.Min, []));
    }

    [Fact]
    public void DecisionScore_IsTheAggregate_UnlessAFrameIsUnderItsMinimum()
    {
        Assert.Equal(0.9, QualificationMetrics.DecisionScore(0.9, 0.7, 0.7));
        Assert.Equal(0.0, QualificationMetrics.DecisionScore(0.99, 0.69, 0.7));
    }

    [Theory]
    [InlineData("surname", "Dell'Orto", "DELLORTO")]
    [InlineData("givenNames", "Jürgen Maria", "JUERGENMARIA")]
    [InlineData("surname.mrz", "Müller", "MUELLER")]
    [InlineData("birthDate", " 1962-07-19 ", "1962-07-19")]
    [InlineData("documentNumber", "ca-00000-aa", "CA00000AA")]
    [InlineData("nationality", "ita", "ITA")]
    [InlineData("sex", "f.", "F")]
    public void NormalizeField_BringsAValueAndTheTruthToOneForm(string field, string value, string expected)
    {
        Assert.Equal(expected, QualificationMetrics.NormalizeField(field, value));
        Assert.Null(QualificationMetrics.NormalizeField(field, null));
    }

    [Fact]
    public void NormalizeField_TheSameNameSpelledTwoWays_IsOneExactReading()
    {
        var exactness = IdentityStatistics.FieldExactness(
            [("surname", "Müller", "MUELLER"), ("surname", "Dell'Orto", "DELL ORTO"), ("surname", "Rossi", "ROSSO")],
            QualificationMetrics.NormalizeField);

        // The umlaut as the MRZ spells it (MUELLER) is the same name, and so is the one with its apostrophe and its space; one letter of another is not.
        Assert.Equal([new NamedRate("surname", new CountedRate(2, 3))], exactness);
    }

    [Fact]
    public void IsSampled_WithFewPairs_TakesThemAll_AndWithManyTheSameDeterministicShare()
    {
        Assert.True(QualificationRunner.IsSampled("g-1", "d-1", 1.0));

        var pairs = Enumerable.Range(0, 200).SelectMany(sequence => Enumerable.Range(0, 100).Select(document => ($"g-{sequence}", $"d-{document}"))).ToList();
        var first = pairs.Where(pair => QualificationRunner.IsSampled(pair.Item1, pair.Item2, 0.25)).ToList();
        var again = pairs.Where(pair => QualificationRunner.IsSampled(pair.Item1, pair.Item2, 0.25)).ToList();

        // Twenty thousand pairs and a share of a quarter: about five thousand, and the very same ones every time, whatever the order they are asked in.
        Assert.Equal(first, again);
        Assert.InRange(first.Count, 4_700, 5_300);
        Assert.Equal(first.Count, Enumerable.Reverse(pairs).Count(pair => QualificationRunner.IsSampled(pair.Item1, pair.Item2, 0.25)));
    }

    // ───── the metrics of a run, on fixed cases ─────

    [Fact]
    public void Compute_OnFixedCases_GivesTheCountsOfEveryMetricAtTheThresholdsOfThePolicy()
    {
        var thresholds = QualificationTestData.Thresholds();
        QualificationFieldResult Field(string name, int exact) => new(name, exact, 1);
        var cases = new List<QualificationCaseResult>
        {
            // A genuine check-in the policy approves (0.90 at the acceptance of 0.85, every frame over 0.70).
            QualificationTestData.Case("g1", QualificationCaseKinds.Genuine, QualificationTestData.Document(true, "passed", Field("surname", 1), Field("documentNumber", 1)), QualificationTestData.Presence(true, 0.90, 0.88), QualificationTestData.Decision("auto_approved", "approve"), matchMs: 100),

            // One under the acceptance: a false non-match, and a surname misread.
            QualificationTestData.Case("g2", QualificationCaseKinds.Genuine, QualificationTestData.Document(true, "passed", Field("surname", 0), Field("documentNumber", 1)), QualificationTestData.Presence(true, 0.80, 0.75), QualificationTestData.Decision("review", "review"), matchMs: 200),

            // A document that was refused, and a sequence that was not valid: a person present that is not accepted.
            QualificationTestData.Case("g3", QualificationCaseKinds.Genuine, QualificationTestData.Document(false, "unknown"), QualificationTestData.Presence(false, validation: false), QualificationTestData.Decision("review", "review")),

            // A person with the document of another one: the face is far, and a frame under the minimum.
            QualificationTestData.Case("i1", QualificationCaseKinds.ImpostorPair, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.30, 0.20), QualificationTestData.Decision("review", "review")),

            // An attack the presence accepted, and the policy approved: a critical false approval. Another of the class is turned away; one of another class too.
            QualificationTestData.Case("a1", QualificationCaseKinds.Attack, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.95, 0.9), QualificationTestData.Decision("auto_approved", "approve"), attackClass: QualificationAttackClasses.PhotoPrint),
            QualificationTestData.Case("a2", QualificationCaseKinds.Attack, QualificationTestData.Document(true), QualificationTestData.Presence(false), QualificationTestData.Decision("review", "review"), attackClass: QualificationAttackClasses.PhotoPrint),
            QualificationTestData.Case("a3", QualificationCaseKinds.Attack, null, QualificationTestData.Presence(false), null, attackClass: QualificationAttackClasses.ScreenReplay),
        };

        // Two trials derived from the sequences: one far, one that is a false match at the policy.
        var trials = new List<QualificationImpostorTrial>
        {
            new("g1", "d-2", 0.40, 0.35, 300, null),
            new("g1", "d-3", 0.90, 0.80, 400, null),
        };

        var metrics = QualificationMetrics.Compute(cases, trials, thresholds, 0.95, QualificationTestData.Models());

        // Of three impostor comparisons (the case, and two trials) one is a false match; of two genuine ones with a face one is not matched.
        Assert.Equal((1L, 3L), (metrics.Fmr.Count, metrics.Fmr.Total));
        Assert.Equal((1L, 2L), (metrics.Fnmr.Count, metrics.Fnmr.Total));
        Assert.Equal(IdentityStatistics.ClopperPearsonUpper(1, 3), metrics.Fmr.UpperBound);

        // The attacks of the class that was tried: one of two accepted; the classes that were not tried are listed with nothing.
        Assert.Equal(QualificationAttackClasses.All.Order(StringComparer.Ordinal), metrics.Apcer.Select(entry => entry.Class));
        Assert.Equal((1L, 2L), (metrics.Apcer.Single(entry => entry.Class == QualificationAttackClasses.PhotoPrint).Rate.Count, metrics.Apcer.Single(entry => entry.Class == QualificationAttackClasses.PhotoPrint).Rate.Total));
        Assert.Equal((0L, 1L), (metrics.Apcer.Single(entry => entry.Class == QualificationAttackClasses.ScreenReplay).Rate.Count, metrics.Apcer.Single(entry => entry.Class == QualificationAttackClasses.ScreenReplay).Rate.Total));
        Assert.Null(metrics.Apcer.Single(entry => entry.Class == QualificationAttackClasses.Mask).Rate.Rate);
        Assert.Equal(0.5, metrics.ApcerMax);

        // Persons present (the three genuine and the impostor pair): one not accepted.
        Assert.Equal((1L, 4L), (metrics.Bpcer.Count, metrics.Bpcer.Total));

        // The fields of the two genuine documents that were read.
        Assert.Equal(
            [("documentNumber", 2L, 2L), ("surname", 1L, 2L)],
            metrics.OcrExactness.Select(entry => (entry.Field, entry.Rate.Count, entry.Rate.Total)));
        Assert.Equal(IdentityStatistics.Wilson(1, 2).Low, metrics.OcrExactness.Single(entry => entry.Field == "surname").Rate.Low);

        // The automation: of the three genuine check-ins the policy would have closed one.
        Assert.Equal((1L, 3L), (metrics.ShadowAutomation.Count, metrics.ShadowAutomation.Total));

        // The document stage: one of the three genuine documents was refused; nothing hostile.
        Assert.Equal((1L, 3L), (metrics.Document.FalseRejects.Count, metrics.Document.FalseRejects.Total));
        Assert.Equal((0L, 0L), (metrics.Document.HostileLetThrough.Count, metrics.Document.HostileLetThrough.Total));
        Assert.Equal(("it-cie-3.0", 3L, 1L), (metrics.Document.ByFormat.Single().Format, metrics.Document.ByFormat.Single().Genuine, metrics.Document.ByFormat.Single().GenuineRefused));

        // A false approval of an attack.
        Assert.Equal(1, metrics.CriticalFalseApprovals.Total);
        Assert.Equal(1, metrics.CriticalFalseApprovals.ByKind[QualificationCaseKinds.Attack]);
        Assert.Equal(0, metrics.CriticalFalseApprovals.ByKind["altered"]);

        // The cases the policy took a decision on, by kind: the two attacks with a document and the person with the document of another one; no altered, no foreign: not measured.
        var decided = metrics.CriticalFalseApprovals.Decided;
        Assert.Equal((2, 1, 0, 0), (decided["attack"], decided["impostor_pair"], decided["altered"], decided["foreign"]));
        Assert.Equal(["altered", "foreign"], metrics.CriticalFalseApprovals.NotMeasured());

        // Every attack had a judgement, and a comparison that names no couple is a couple of its own.
        Assert.All(metrics.AttacksNotJudged, entry => Assert.Equal(0, entry.NotJudged));
        Assert.Equal((1L, 3L), (metrics.FmrCouples.Count, metrics.FmrCouples.Total));

        // The time of the comparison: the two cases and the two trials, by nearest rank.
        var match = metrics.Latency.Single(stage => stage.Stage == QualificationStages.PresenceMatch);
        Assert.Equal((4, 200.0, 400.0, 400.0), (match.Count, match.P50Ms, match.P95Ms, match.MaxMs));
    }

    [Fact]
    public void Compute_AlteredAndForeignDocuments_AreCountedAsHostile_AndLeftOutOfTheGenuineRates()
    {
        var cases = new List<QualificationCaseResult>
        {
            QualificationTestData.Case("h1", QualificationCaseKinds.Genuine, QualificationTestData.Document(true, "passed", new QualificationFieldResult("surname", 0, 1)), QualificationTestData.Presence(true, 0.9, 0.9), QualificationTestData.Decision("auto_approved", "approve"), altered: true),
            QualificationTestData.Case("h2", QualificationCaseKinds.Genuine, QualificationTestData.Document(false, "unknown"), QualificationTestData.Presence(true, 0.9, 0.9), QualificationTestData.Decision("review", "review"), foreign: true),
        };

        var metrics = QualificationMetrics.Compute(cases, [], QualificationTestData.Thresholds(), 0.95, QualificationTestData.Models());

        // Neither is a genuine check-in: no automation, no false non-match, no field read; the altered one that the document stage let through and the policy approved is a critical case.
        Assert.Equal((0L, 0L), (metrics.ShadowAutomation.Count, metrics.ShadowAutomation.Total));
        Assert.Equal(0L, metrics.Fnmr.Total);
        Assert.Empty(metrics.OcrExactness);
        Assert.Equal((1L, 2L), (metrics.Document.HostileLetThrough.Count, metrics.Document.HostileLetThrough.Total));
        Assert.Equal(1, metrics.CriticalFalseApprovals.Total);
        Assert.Equal(1, metrics.CriticalFalseApprovals.ByKind["altered"]);
    }

    [Fact]
    public void Rebound_TakesTheBoundsAgainFromTheCounts_AtAnotherConfidence()
    {
        var metrics = QualificationMetrics.Compute([], [], QualificationTestData.Thresholds(), 0.95, QualificationTestData.Models());
        var withTrials = metrics with { Fmr = QualificationBoundedRate.Of(0, 30_000, 0.95) };

        var rebound = QualificationMetrics.Rebound(withTrials, 0.99);

        Assert.Equal(IdentityStatistics.ClopperPearsonUpper(0, 30_000, 0.99), rebound.Fmr.UpperBound);
        Assert.True(rebound.Fmr.UpperBound > withTrials.Fmr.UpperBound);
        Assert.Equal(withTrials.Fmr.Count, rebound.Fmr.Count);
    }

    // ───── the candidate model at the same acceptances ─────

    [Fact]
    public void CompareAtTheSameAcceptances_TheCandidateIsHeldToTheNumberOfNegativesTheReferenceAccepts()
    {
        (double Reference, double Candidate)[] positives = [(0.9, 0.95), (0.8, 0.97), (0.6, 0.90)];
        (double Reference, double Candidate)[] negatives = [(0.9, 0.50), (0.3, 0.40), (0.2, 0.10)];

        var comparison = QualificationMetrics.CompareAtTheSameAcceptances("match", "SFace", "Other", 0.85, positives, negatives)!;

        // The reference accepts one negative and rejects two positives at the threshold of the policy.
        Assert.Equal(new QualificationOperatingPoint(0.85, 1, 3, 2, 3), comparison.ReferencePoint);

        // The candidate may accept one negative too: its threshold is just above the second highest negative (0.40), and it rejects no positive.
        Assert.Equal(Math.BitIncrement(0.40), comparison.CandidatePoint.Threshold);
        Assert.Equal((1L, 0L), (comparison.CandidatePoint.FalseAccepts, comparison.CandidatePoint.FalseRejects));
        Assert.True(comparison.CandidateRejectsFewer);
        Assert.Equal(("SFace", "Other", "match"), (comparison.Reference, comparison.Candidate, comparison.Measure));
    }

    [Fact]
    public void CompareAtTheSameAcceptances_AWorseCandidate_RejectsNoFewer_AndNothingToCompareIsNothing()
    {
        (double Reference, double Candidate)[] positives = [(0.9, 0.60), (0.8, 0.55)];
        (double Reference, double Candidate)[] negatives = [(0.3, 0.70), (0.2, 0.10)];

        var worse = QualificationMetrics.CompareAtTheSameAcceptances("liveness", "A", "B", 0.7, positives, negatives)!;

        // The reference accepts no negative; the candidate must accept none: above 0.70, and both positives are under it.
        Assert.Equal(0, worse.ReferencePoint.FalseAccepts);
        Assert.Equal((0L, 2L), (worse.CandidatePoint.FalseAccepts, worse.CandidatePoint.FalseRejects));
        Assert.False(worse.CandidateRejectsFewer);
        Assert.Null(QualificationMetrics.CompareAtTheSameAcceptances("match", "A", "B", 0.7, positives, []));
        Assert.Null(QualificationMetrics.CompareAtTheSameAcceptances("match", "A", "B", 0.7, [], negatives));
    }

    [Fact]
    public void Compute_PairedScoresOfACandidate_BecomeAComparisonAtTheSameAcceptances()
    {
        var thresholds = QualificationTestData.Thresholds();
        var cases = new List<QualificationCaseResult>
        {
            QualificationTestData.Case("g1", QualificationCaseKinds.Genuine, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.9, 0.9), null, pairedMatch: new QualificationPairedScores([0.9, 0.9, 0.9], [0.95, 0.95, 0.95], null)),
            QualificationTestData.Case("g2", QualificationCaseKinds.Genuine, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.8, 0.8), null, pairedMatch: new QualificationPairedScores([0.8, 0.8, 0.8], [0.97, 0.97, 0.97], null)),
            QualificationTestData.Case("i1", QualificationCaseKinds.ImpostorPair, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.3, 0.3), null, pairedMatch: new QualificationPairedScores([0.3, 0.3, 0.3], [0.5, 0.5, 0.5], null)),
        };
        var trials = new List<QualificationImpostorTrial> { new("g1", "d-2", 0.2, 0.2, 10, new QualificationPairedScores([0.2, 0.2, 0.2], [0.4, 0.4, 0.4], null)) };

        var comparison = Assert.Single(QualificationMetrics.Compute(cases, trials, thresholds, 0.95, QualificationTestData.Models(candidate: true)).CandidateComparisons);

        // The reference rejects the second genuine comparison (0.80 under 0.85); the candidate, held to the same (no) false acceptance, rejects none.
        Assert.Equal("match", comparison.Measure);
        Assert.Equal((0L, 2L, 1L), (comparison.ReferencePoint.FalseAccepts, comparison.ReferencePoint.Positives, comparison.ReferencePoint.FalseRejects));
        Assert.Equal((0L, 0L), (comparison.CandidatePoint.FalseAccepts, comparison.CandidatePoint.FalseRejects));
        Assert.True(comparison.CandidateRejectsFewer);
    }

    // ───── what a run could not judge, the cases the criteria are measured on, the couples of persons ─────

    [Fact]
    public void WasJudged_ACaseThatBrokeOrWhosePresenceCouldNotBeRead_OrJudged_IsNotJudged()
    {
        QualificationCaseResult Attack(QualificationPresenceResult? presence, string? error = null)
        {
            return QualificationTestData.Case("a", QualificationCaseKinds.Attack, null, presence, null, attackClass: QualificationAttackClasses.PhotoPrint, error: error);
        }

        // Judged: refused by the validation for what the recording shows, its frames not chosen, its liveness failed or passed.
        Assert.True(QualificationMetrics.WasJudged(Attack(QualificationTestData.Presence(false, validation: false))));
        Assert.True(QualificationMetrics.WasJudged(Attack(QualificationTestData.Presence(false, validation: true, selectionFailure: "presence_quality"))));
        Assert.True(QualificationMetrics.WasJudged(Attack(QualificationTestData.Presence(false, liveness: [0.1, 0.2]))));
        Assert.True(QualificationMetrics.WasJudged(Attack(QualificationTestData.Presence(true, 0.9, 0.9))));

        // Not judged: an exception, a sequence that could not be read, a validation or a liveness that was not available, no presence part at all.
        Assert.False(QualificationMetrics.WasJudged(Attack(QualificationTestData.Presence(false), error: "InvalidOperationException")));
        Assert.False(QualificationMetrics.WasJudged(Attack(QualificationTestData.Presence(false, intakeError: "presence_sequence_invalid"))));
        Assert.False(QualificationMetrics.WasJudged(Attack(QualificationTestData.Presence(false, validationUnavailable: true))));
        Assert.False(QualificationMetrics.WasJudged(Attack(QualificationTestData.Presence(false, livenessUnavailable: true))));
        Assert.False(QualificationMetrics.WasJudged(Attack(null)));
    }

    /// <summary>Proof (a target that cannot be measured fails): an attack that could not be judged is no rejection, so it is out of the total of its class and is counted apart.</summary>
    [Fact]
    public void Compute_AnAttackThatCouldNotBeJudged_IsOutOfTheTotalOfItsClass_AndCountedApart()
    {
        QualificationCaseResult Attack(string id, QualificationPresenceResult presence, string attackClass = QualificationAttackClasses.PhotoPrint, string? error = null)
        {
            return QualificationTestData.Case(id, QualificationCaseKinds.Attack, null, presence, null, attackClass: attackClass, error: error);
        }

        var cases = new List<QualificationCaseResult>
        {
            Attack("a1", QualificationTestData.Presence(true, 0.9, 0.9)),
            Attack("a2", QualificationTestData.Presence(false)),
            Attack("a3", QualificationTestData.Presence(false), error: "InvalidOperationException"),
            Attack("a4", QualificationTestData.Presence(false, validationUnavailable: true)),
            Attack("a5", QualificationTestData.Presence(false, intakeError: "presence_sequence_too_large")),
            Attack("a6", QualificationTestData.Presence(false, livenessUnavailable: true), QualificationAttackClasses.ScreenReplay),
        };

        var metrics = QualificationMetrics.Compute(cases, [], QualificationTestData.Thresholds(), 0.95, QualificationTestData.Models());

        // Of the six attacks two of the photo class were judged (one accepted); the other three and the one of the screen class were not.
        var photo = metrics.Apcer.Single(entry => entry.Class == QualificationAttackClasses.PhotoPrint).Rate;
        var screen = metrics.Apcer.Single(entry => entry.Class == QualificationAttackClasses.ScreenReplay).Rate;
        Assert.Equal((1L, 2L), (photo.Count, photo.Total));
        Assert.Equal((0L, 0L), (screen.Count, screen.Total));
        Assert.Equal(0.5, metrics.ApcerMax);
        Assert.Equal(
            [(QualificationAttackClasses.PhotoPrint, 3L, 5L), (QualificationAttackClasses.ScreenReplay, 1L, 1L)],
            metrics.AttacksNotJudged.Where(entry => entry.Total > 0).Select(entry => (entry.Class, entry.NotJudged, entry.Total)));
        Assert.Equal(QualificationAttackClasses.All.Order(StringComparer.Ordinal), metrics.AttacksNotJudged.Select(entry => entry.Class));
    }

    /// <summary>Proof (data): the exactness of a field is measured on the genuine captures of the owner of the document: an attack, a person with the document of another, an altered or a foreign one show pictures a genuine capture shows already.</summary>
    [Fact]
    public void Compute_TheExactnessOfTheFields_IsMeasuredOnTheGenuineCapturesOfTheOwnerOnly()
    {
        QualificationDocumentResult Read(int exact) => QualificationTestData.Document(true, "passed", new QualificationFieldResult("surname", exact, 1));
        var cases = new List<QualificationCaseResult>
        {
            QualificationTestData.Case("g1", QualificationCaseKinds.Genuine, Read(1), QualificationTestData.Presence(true, 0.9, 0.9)),
            QualificationTestData.Case("g2", QualificationCaseKinds.Genuine, Read(0), QualificationTestData.Presence(true, 0.9, 0.9)),
            QualificationTestData.Case("a1", QualificationCaseKinds.Attack, Read(1), QualificationTestData.Presence(false), attackClass: QualificationAttackClasses.PhotoPrint),
            QualificationTestData.Case("i1", QualificationCaseKinds.ImpostorPair, Read(1), QualificationTestData.Presence(true, 0.3, 0.3)),
            QualificationTestData.Case("h1", QualificationCaseKinds.Genuine, Read(1), QualificationTestData.Presence(true, 0.9, 0.9), altered: true),
            QualificationTestData.Case("f1", QualificationCaseKinds.Genuine, Read(1), QualificationTestData.Presence(true, 0.9, 0.9), foreign: true),
        };

        var metrics = QualificationMetrics.Compute(cases, [], QualificationTestData.Thresholds(), 0.95, QualificationTestData.Models());

        var surname = Assert.Single(metrics.OcrExactness);
        Assert.Equal(("surname", 1L, 2L), (surname.Field, surname.Rate.Count, surname.Rate.Total));
    }

    /// <summary>Proof (the same name is the same name): a name read in the other spelling the MRZ may use is exact, as the coherence checker takes it; another name is not, and a number is not reconciled.</summary>
    [Fact]
    public void ExactnessRates_ANameInTheOtherSpellingOfTheMrz_IsExact_AndAnotherNameIsNot()
    {
        var rates = QualificationMetrics.ExactnessRates(
        [
            ("surname", "Müller", "MULLER"),
            ("surname", "Müller", "MUELLER"),
            ("surname", "Müller", "Müller"),
            ("surname", "Rossi", "ROSSO"),
            ("surname.mrz", "Jürgen", "JUERGEN"),
            ("givenNames", "Zoë", "ZOE"),
            ("documentNumber", "CA00000AA", "CA0OO00AA"),
        ]);

        Assert.Equal(
            [("documentNumber", 0L, 1L), ("givenNames", 1L, 1L), ("surname", 3L, 4L), ("surname.mrz", 1L, 1L)],
            rates.Select(rate => (rate.Name, rate.Rate.Count, rate.Rate.Total)));

        // The reconciliation is of names only, and only of the same name: the number and another name are as read.
        Assert.Equal("Müller", QualificationMetrics.SpelledAsExpected("surname", "Müller", "MULLER"));
        Assert.Equal("ROSSO", QualificationMetrics.SpelledAsExpected("surname", "Rossi", "ROSSO"));
        Assert.Equal("CA0OO00AA", QualificationMetrics.SpelledAsExpected("documentNumber", "CA00000AA", "CA0OO00AA"));
        Assert.Null(QualificationMetrics.SpelledAsExpected("surname", "Müller", null));
    }

    /// <summary>Proof (a target that cannot be measured fails): the critical approvals have their DENOMINATORS, the cases of each kind the policy took a decision on.</summary>
    [Fact]
    public void Compute_TheCriticalApprovals_AreCountedOverTheCasesDecided_OfEachKind()
    {
        var cases = new List<QualificationCaseResult>
        {
            // Decided: two attacks (one approved), a person with the document of another, an altered document that was approved, and a foreign image that was also altered.
            QualificationTestData.Case("a1", QualificationCaseKinds.Attack, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.9, 0.9), QualificationTestData.Decision("auto_approved", "approve"), attackClass: QualificationAttackClasses.PhotoPrint),
            QualificationTestData.Case("a2", QualificationCaseKinds.Attack, QualificationTestData.Document(true), QualificationTestData.Presence(false), QualificationTestData.Decision("review", "review"), attackClass: QualificationAttackClasses.Synthetic),
            QualificationTestData.Case("i1", QualificationCaseKinds.ImpostorPair, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.3, 0.3), QualificationTestData.Decision("review", "review")),
            QualificationTestData.Case("h1", QualificationCaseKinds.Genuine, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.9, 0.9), QualificationTestData.Decision("auto_approved", "approve"), altered: true),
            QualificationTestData.Case("f1", QualificationCaseKinds.Genuine, QualificationTestData.Document(false, "unknown"), QualificationTestData.Presence(false), QualificationTestData.Decision("review", "review"), altered: true, foreign: true),

            // Not decided: an attack with no document, one that broke, and an altered document with no sequence: they are no denominator.
            QualificationTestData.Case("a3", QualificationCaseKinds.Attack, null, QualificationTestData.Presence(false), null, attackClass: QualificationAttackClasses.VideoReplay),
            QualificationTestData.Case("a4", QualificationCaseKinds.Attack, QualificationTestData.Document(true), QualificationTestData.Presence(false), null, attackClass: QualificationAttackClasses.ScreenReplay, error: "IOException"),
            QualificationTestData.Case("h2", QualificationCaseKinds.Genuine, QualificationTestData.Document(true), null, null, altered: true),
        };

        var critical = QualificationMetrics.Compute(cases, [], QualificationTestData.Thresholds(), 0.95, QualificationTestData.Models()).CriticalFalseApprovals;

        Assert.Equal(2, critical.Total);
        Assert.Equal((1, 0, 1, 0), (critical.ByKind["attack"], critical.ByKind["impostor_pair"], critical.ByKind["altered"], critical.ByKind["foreign"]));
        Assert.Equal((2, 1, 2, 1), (critical.Decided["attack"], critical.Decided["impostor_pair"], critical.Decided["altered"], critical.Decided["foreign"]));
        Assert.Empty(critical.NotMeasured());

        // With no case decided of a kind, that kind is not measured.
        var withoutForeign = QualificationMetrics.Compute([.. cases.Where(candidate => !candidate.Foreign)], [], QualificationTestData.Thresholds(), 0.95, QualificationTestData.Models()).CriticalFalseApprovals;
        Assert.Equal(["foreign"], withoutForeign.NotMeasured());
        Assert.Equal(QualificationCriticalKinds.All, QualificationMetrics.Compute([], [], QualificationTestData.Thresholds(), 0.95, QualificationTestData.Models()).CriticalFalseApprovals.NotMeasured());
    }

    /// <summary>Proof (independence): the comparisons of one couple of persons are one piece of evidence, and a couple is a false match when any comparison of it is.</summary>
    [Fact]
    public void Compute_TheFalseMatchRate_IsAlsoCountedOverTheDistinctCouplesOfPersons()
    {
        var couple1 = QualificationMetrics.CoupleKey("p1", "p2");
        var couple2 = QualificationMetrics.CoupleKey("p2", "p1");
        var couple3 = QualificationMetrics.CoupleKey("p3", "p4");
        var trials = new List<QualificationImpostorTrial>
        {
            // Five comparisons of the same couple of persons, one of them a false match (0.90 at the acceptance of 0.85).
            new("g1", "d-2", 0.40, 0.35, 1, null, couple1),
            new("g1b", "d-2", 0.41, 0.36, 1, null, couple1),
            new("g1", "d-2b", 0.42, 0.37, 1, null, couple1),
            new("g1b", "d-2b", 0.90, 0.80, 1, null, couple1),
            new("g1c", "d-2", 0.43, 0.38, 1, null, couple1),

            // The other direction of the same two persons is another couple; a third couple has two comparisons and no false match.
            new("g2", "d-1", 0.30, 0.30, 1, null, couple2),
            new("g3", "d-4", 0.20, 0.20, 1, null, couple3),
            new("g3b", "d-4", 0.21, 0.21, 1, null, couple3),
        };
        var cases = new List<QualificationCaseResult>
        {
            // A person with the document of another one: its own couple, with a score under the acceptance.
            QualificationTestData.Case("i1", QualificationCaseKinds.ImpostorPair, QualificationTestData.Document(true), QualificationTestData.Presence(true, 0.5, 0.5), couple: QualificationMetrics.CoupleKey("p5", "p6")),
        };

        var metrics = QualificationMetrics.Compute(cases, trials, QualificationTestData.Thresholds(), 0.95, QualificationTestData.Models());

        // Nine comparisons, one of them a false match; four couples of persons, one of them a false match.
        Assert.Equal((1L, 9L), (metrics.Fmr.Count, metrics.Fmr.Total));
        Assert.Equal((1L, 4L), (metrics.FmrCouples.Count, metrics.FmrCouples.Total));
        Assert.Equal(IdentityStatistics.ClopperPearsonUpper(1, 4), metrics.FmrCouples.UpperBound);
    }

    [Fact]
    public void CoupleKey_IsTheSameForTheSameCouple_AnotherForTheOtherDirection_AndNamesNobody()
    {
        var key = QualificationMetrics.CoupleKey("person-1", "person-2");

        Assert.Equal(key, QualificationMetrics.CoupleKey("person-1", "person-2"));
        Assert.NotEqual(key, QualificationMetrics.CoupleKey("person-2", "person-1"));
        Assert.NotEqual(key, QualificationMetrics.CoupleKey("person-1", "person-3"));
        Assert.Equal(24, key.Length);
        Assert.All(key, character => Assert.True(char.IsAsciiHexDigitLower(character)));
        Assert.DoesNotContain("person", key, StringComparison.Ordinal);
    }
}

/// <summary>Cases, results and models written in code for the tests of the qualification toolkit: ids, codes and numbers, never a picture or a value of a person.</summary>
internal static class QualificationTestData
{
    public static readonly DateTimeOffset RunAt = new(2026, 12, 5, 10, 0, 0, TimeSpan.Zero);

    public const string PolicyHash = "d2d343e2bf661f316c8e3e4a91252ecf6c9da1e669766609e9e393978fd04426";

    public const string ManifestHash = "5555555555555555555555555555555555555555555555555555555555555555";

    public static readonly string DetectorHash = new('1', 64);
    public static readonly string AntiSpoofingHash = new('2', 64);
    public static readonly string RecognitionHash = new('3', 64);
    public static readonly string CandidateHash = new('4', 64);

    /// <summary>The thresholds of the committed policy.</summary>
    public static QualificationThresholds Thresholds() => QualificationThresholds.From(IdentityTestPolicy.Shipped());

    /// <summary>The policy of the committed file, calibrated (not provisional): what a lock may be made from.</summary>
    public static IdentityDecisionPolicy Calibrated() => IdentityTestPolicy.Shipped() with { Provisional = false };

    /// <summary>The three models of the decisions, loaded with their weights, and a loaded candidate of the recognition model when asked.</summary>
    public static IReadOnlyList<QualificationModelInfo> Models(bool candidate = false)
    {
        var models = new List<QualificationModelInfo>
        {
            new(BiometricModelRoles.FaceDetector, "YuNet", "2023mar", "MIT", "Trained on WIDER FACE.", DetectorHash.ToUpperInvariant(), true, null),
            new(BiometricModelRoles.AntiSpoofing, "MiniFASNetV2", "2.7_80x80", "Apache-2.0", "Not published.", AntiSpoofingHash.ToUpperInvariant(), true, null),
            new(BiometricModelRoles.FaceRecognition, "SFace", "2021dec", "Apache-2.0", "OpenCV Zoo.", RecognitionHash.ToUpperInvariant(), true, null),
        };
        if (candidate)
            models.Add(new QualificationModelInfo(BiometricModelRoles.FaceRecognitionCandidate, "Other", "1", "to be confirmed", "to be confirmed", CandidateHash.ToUpperInvariant(), true, null));

        return models;
    }

    /// <summary>The snapshot of a store that has the three models loaded (and the candidate, when asked).</summary>
    public static BiometricModelsSnapshot Snapshot(bool candidate = false)
    {
        var infos = Models(candidate).Select(model => new BiometricModelInfo(model.Role, model.Name, model.Version, model.License, model.Provenance, model.WeightSha256, model.Loaded, model.FailureCode)).ToList();
        return new BiometricModelsSnapshot(BiometricModelsState.Ready, null, [.. infos.Take(3)]) { Candidates = [.. infos.Skip(3)] };
    }

    public static QualificationPresenceResult Presence(
        bool accepted,
        double? similarity = null,
        double? min = null,
        IReadOnlyList<double>? liveness = null,
        bool validation = true,
        string? selectionFailure = null,
        bool validationUnavailable = false,
        bool livenessUnavailable = false,
        string? intakeError = null)
    {
        return new QualificationPresenceResult(
            intakeError,
            25,
            3000,
            validation,
            validationUnavailable,
            [],
            selectionFailure,
            [],
            [],
            livenessUnavailable ? "unavailable" : accepted ? "passed" : "failed",
            liveness ?? [],
            similarity is null ? "not_run" : "ok",
            similarity,
            min,
            similarity is null ? null : similarity >= 0.85 && min >= 0.7,
            accepted);
    }

    public static QualificationDocumentResult Document(bool complete, string coherence = "passed", params QualificationFieldResult[] fields)
    {
        return new QualificationDocumentResult("it-cie-3.0", complete, coherence, true, [], [], fields);
    }

    public static QualificationDecisionResult Decision(string final, string shadow)
    {
        return new QualificationDecisionResult(final, shadow, "automatic", final == "auto_approved" ? "auto_approved" : "review", [], []);
    }

    public static QualificationCaseResult Case(
        string id,
        string kind,
        QualificationDocumentResult? document,
        QualificationPresenceResult? presence,
        QualificationDecisionResult? decision = null,
        string? attackClass = null,
        bool altered = false,
        bool foreign = false,
        double? matchMs = null,
        QualificationPairedScores? pairedMatch = null,
        QualificationPairedScores? pairedLiveness = null,
        string? error = null,
        string? couple = null)
    {
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        if (matchMs is { } ms)
            stages[QualificationStages.PresenceMatch] = ms;

        return new QualificationCaseResult(id, kind, attackClass, "web:desktop-chromium", document?.Format, altered, foreign, document, presence, decision, pairedMatch, pairedLiveness, stages, error, couple);
    }

    public static QualificationDatasetSummary Dataset(string split = "test")
    {
        var attacks = QualificationAttackClasses.All.ToDictionary(attackClass => attackClass, attackClass => attackClass == QualificationAttackClasses.Mask ? 0 : 400, StringComparer.Ordinal);
        var wholeCheckIns = new Dictionary<string, int> { [QualificationCriticalKinds.Attack] = 1600, [QualificationCriticalKinds.ImpostorPair] = 300, [QualificationCriticalKinds.Altered] = 12, [QualificationCriticalKinds.Foreign] = 6 };
        return new QualificationDatasetSummary(
            [.. QualificationSplits.All.Select(name => new QualificationSplitCounts(name, 120, 2400, 1000, 300, 280, 12, 6, attacks, wholeCheckIns))],
            new QualificationEvaluatedSummary(
                2400,
                new Dictionary<string, int> { [QualificationCaseKinds.Genuine] = 1700, [QualificationCaseKinds.Attack] = 1600 },
                new Dictionary<string, int> { [QualificationAttackClasses.PhotoPrint] = 400 },
                new Dictionary<string, int> { ["it-cie-3.0"] = 2400 },
                new Dictionary<string, int> { ["web:desktop-chromium"] = 2400 }));
    }

    /// <summary>The results of a run with the metrics given (those of the cases when none is).</summary>
    public static QualificationResults Results(
        string split,
        IReadOnlyList<QualificationCaseResult> cases,
        IReadOnlyList<QualificationImpostorTrial> trials,
        QualificationMetricsBlock? metrics = null,
        IReadOnlyList<QualificationModelInfo>? models = null,
        bool provisional = false)
    {
        var thresholds = Thresholds();
        models ??= Models();
        return new QualificationResults(
            QualificationResults.CurrentSchemaVersion,
            "synthetic-dataset",
            "1",
            ManifestHash,
            split,
            RunAt,
            new DateOnly(2026, 12, 5),
            0.95,
            new QualificationPolicyInfo("identity-policy-2026.11-1", PolicyHash, provisional, ["surname", "givenNames", "birthDate", "documentNumber"]),
            thresholds,
            models,
            Dataset(split),
            new QualificationImpostorSampling(40_000, 40_000, false, 50_000, 0),
            cases,
            trials,
            metrics ?? QualificationMetrics.Compute(cases, trials, thresholds, 0.95, models));
    }
}
