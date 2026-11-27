using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Identity.Statistics;
using Casazen.Tests.Qualification;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F01/US02/T02 (NF-198, NF-199, D12): the report of a candidate version. Built from results, a lock and the targets: it lists the weights (SHA-256, licence, provenance), the policy (version,
/// canonical hash), the thresholds, the dataset, the metrics with their intervals and a verdict for each target, and it ends with its own SHA-256, stable for the same input. These tests work
/// on fixed numbers: no model and no dataset.
/// </summary>
public sealed class CandidateVersionReportTests : IDisposable
{
    private const double Confidence = 0.95;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "casazen-candidate-report-" + Guid.NewGuid().ToString("N"));

    public CandidateVersionReportTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static QualificationBoundedRate Bounded(long count, long total) => QualificationBoundedRate.Of(count, total, Confidence);

    private static QualificationWilsonRate Wilson(long count, long total) => QualificationWilsonRate.Of(count, total, Confidence);

    /// <summary>The metrics of a qualification that meets every target of the proposals of the specification.</summary>
    private static QualificationMetricsBlock Passing()
    {
        return new QualificationMetricsBlock(
            Bounded(0, 40_000),
            Bounded(30, 1_000),
            [
                new QualificationClassRate(QualificationAttackClasses.Mask, Bounded(0, 0)),
                new QualificationClassRate(QualificationAttackClasses.Other, Bounded(0, 0)),
                new QualificationClassRate(QualificationAttackClasses.PhotoPrint, Bounded(0, 400)),
                new QualificationClassRate(QualificationAttackClasses.ScreenReplay, Bounded(0, 350)),
                new QualificationClassRate(QualificationAttackClasses.Synthetic, Bounded(0, 300)),
                new QualificationClassRate(QualificationAttackClasses.VideoReplay, Bounded(0, 320)),
            ],
            0.0,
            Bounded(10, 1_000),
            [
                new QualificationFieldRate("birthDate", Wilson(499, 500)),
                new QualificationFieldRate("documentNumber", Wilson(500, 500)),
                new QualificationFieldRate("givenNames", Wilson(498, 500)),
                new QualificationFieldRate("surname", Wilson(497, 500)),
                new QualificationFieldRate("surname.mrz", Wilson(500, 500)),
            ],
            Wilson(450, 500),
            new QualificationDocumentMetrics(Bounded(2, 600), Bounded(0, 18), [new QualificationFormatDocumentRate("it-cie-3.0", 600, 2, 18, 0)]),
            new QualificationCriticalApprovals(0, new Dictionary<string, int> { ["altered"] = 0, ["attack"] = 0, ["foreign"] = 0, ["impostor_pair"] = 0 }),
            [new QualificationLatency(QualificationStages.PresenceMatch, 4000, 120.0, 310.0, 900.0)],
            []);
    }

    private static QualificationLock Frozen(string? weightsHash = null)
    {
        var frozen = QualificationLock.From(
            QualificationTestData.Calibrated(),
            QualificationTestData.PolicyHash,
            QualificationTestData.Snapshot(),
            QualificationTestData.ManifestHash,
            120,
            QualificationTestData.RunAt.AddDays(-3));
        return weightsHash is null ? frozen : frozen with { WeightsHash = weightsHash };
    }

    private static QualificationResults Results(QualificationMetricsBlock metrics, IReadOnlyList<QualificationCaseResult>? cases = null)
    {
        return QualificationTestData.Results(QualificationSplits.Test, cases ?? [], [], metrics);
    }

    private static CandidateVersionReport Report(QualificationMetricsBlock metrics, QualificationLock? frozen = null, IReadOnlyList<QualificationCaseResult>? cases = null)
    {
        return CandidateVersionReport.Build(Results(metrics, cases), frozen ?? Frozen(), QualificationTargets.Load());
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text)));

    [Fact]
    public void Report_ListsTheWeightsThePolicyTheThresholdsTheDatasetTheMetricsAndAVerdictPerTarget()
    {
        var report = Report(Passing());
        var text = report.Markdown;

        Assert.StartsWith("# Candidate version report\n", text, StringComparison.Ordinal);
        foreach (var heading in new[] { "## Candidate version", "## Dataset", "## Metrics (95% confidence)", "## Targets" })
            Assert.Contains(heading, text, StringComparison.Ordinal);

        // The weights of every model with its licence and provenance, the policy with its version and canonical hash, the combined hash of the weights.
        foreach (var model in QualificationTestData.Models())
        {
            Assert.Contains(model.WeightSha256!, text, StringComparison.Ordinal);
            Assert.Contains(model.License, text, StringComparison.Ordinal);
            Assert.Contains(model.Provenance, text, StringComparison.Ordinal);
        }

        Assert.Contains("`identity-policy-2026.11-1`", text, StringComparison.Ordinal);
        Assert.Contains(QualificationTestData.PolicyHash, text, StringComparison.Ordinal);
        Assert.Contains("provisional: no", text, StringComparison.Ordinal);
        Assert.Contains("Run at 2026-12-05T10:00:00Z, documents judged on 2026-12-05. Lock created at 2026-12-02T10:00:00Z.", text, StringComparison.Ordinal);
        Assert.Contains(Frozen().WeightsHash, text, StringComparison.Ordinal);
        Assert.Contains("accept 0.85, review floor 0.7, least similarity of each frame 0.7, least liveness 0.7", text, StringComparison.Ordinal);
        Assert.Contains("similarity of the chosen frames by `median` over 3 frames; liveness by `all` over 5 frames", text, StringComparison.Ordinal);

        // The dataset by split, class, format and channel; the sample of the impostor trials.
        Assert.Contains("development: persons=120 captures=2400", text, StringComparison.Ordinal);
        Assert.Contains("- by document format: it-cie-3.0=2400", text, StringComparison.Ordinal);
        Assert.Contains("- by channel: web:desktop-chromium=2400", text, StringComparison.Ordinal);
        Assert.Contains("40000 run of 40000 possible pairs (sample: no, limit 50000)", text, StringComparison.Ordinal);

        // The metrics with their intervals.
        Assert.Contains("- False match rate (FMR) at the policy: 0/40000 = 0, upper bound 0.000075", text, StringComparison.Ordinal);
        Assert.Contains("- False non-match rate (FNMR) at the policy: 30/1000 = 0.03, upper bound ", text, StringComparison.Ordinal);
        Assert.Contains("  - photo_print: 0/400 = 0, upper bound 0.007461", text, StringComparison.Ordinal);
        Assert.Contains("  - mask: 0/0 = n/a, upper bound 1", text, StringComparison.Ordinal);
        Assert.Contains("  - surname: 497/500 = 0.994, Wilson interval ", text, StringComparison.Ordinal);
        Assert.Contains("- Shadow automation (check-ins the policy would close): 450/500 = 0.9, Wilson interval ", text, StringComparison.Ordinal);
        Assert.Contains("  - presence_match: 4000 runs, 120.0, 310.0, 900.0.", text, StringComparison.Ordinal);

        // A verdict for every target of the file, and for every class and field they are measured on.
        Assert.Equal(
            ["fmr", "apcer.photo_print", "apcer.screen_replay", "apcer.video_replay", "apcer.synthetic", "automation", "ocr.surname", "ocr.surname.mrz", "ocr.givenNames", "ocr.birthDate", "ocr.documentNumber", "critical_false_approvals"],
            report.Verdicts.Select(verdict => verdict.Target));
        Assert.All(report.Verdicts, verdict => Assert.True(verdict.Pass, verdict.Target));
        Assert.True(report.AllTargetsPassed);
        Assert.Contains("Every target holds.", text, StringComparison.Ordinal);
        Assert.Contains("Frozen configuration: the run used exactly the configuration of the lock.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_ATargetThatFails_IsFailedWithItsReason_AndTheOthersStillHold()
    {
        var metrics = Passing() with
        {
            // Too few comparisons to show a limit of 1 in 10,000 even with no false match; an attack of a class accepted; too few of another; none of a third.
            Fmr = Bounded(0, 20_000),
            Apcer =
            [
                new QualificationClassRate(QualificationAttackClasses.PhotoPrint, Bounded(3, 400)),
                new QualificationClassRate(QualificationAttackClasses.ScreenReplay, Bounded(0, 350)),
                new QualificationClassRate(QualificationAttackClasses.Synthetic, Bounded(0, 120)),
            ],
            ShadowAutomation = Wilson(400, 500),
            OcrExactness = [new QualificationFieldRate("birthDate", Wilson(499, 500)), new QualificationFieldRate("givenNames", Wilson(495, 500)), new QualificationFieldRate("surname", Wilson(497, 500))],
            CriticalFalseApprovals = new QualificationCriticalApprovals(1, new Dictionary<string, int> { ["attack"] = 1 }),
        };

        var report = Report(metrics);
        var verdicts = report.Verdicts.ToDictionary(verdict => verdict.Target, StringComparer.Ordinal);

        Assert.False(report.AllTargetsPassed);
        Assert.False(verdicts["fmr"].Pass);
        Assert.Contains("29956 independent comparisons", verdicts["fmr"].Note, StringComparison.Ordinal);
        Assert.False(verdicts["apcer.photo_print"].Pass);
        Assert.True(verdicts["apcer.screen_replay"].Pass);
        Assert.False(verdicts["apcer.synthetic"].Pass);
        Assert.Equal("too few attacks of this class to show the limit", verdicts["apcer.synthetic"].Note);
        Assert.False(verdicts["apcer.video_replay"].Pass);
        Assert.Equal("no attack of this class was tried", verdicts["apcer.video_replay"].Note);
        Assert.False(verdicts["automation"].Pass);
        Assert.False(verdicts["ocr.givenNames"].Pass);
        Assert.True(verdicts["ocr.surname"].Pass);
        Assert.True(verdicts["ocr.birthDate"].Pass);

        // A field that was never read is not measured, and a target that is not measured fails.
        Assert.False(verdicts["ocr.documentNumber"].Pass);
        Assert.Equal("not measured", verdicts["ocr.documentNumber"].Measured);
        Assert.False(verdicts["critical_false_approvals"].Pass);
        Assert.Contains("| apcer.photo_print |", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("FAIL", report.Markdown, StringComparison.Ordinal);
        Assert.Matches(@"\d+ of \d+ verdicts do not hold\.", report.Markdown);
    }

    [Fact]
    public void Report_TheIntervalsAreThoseOfTheConfidenceOfTheTargets_WhateverTheRunUsed()
    {
        var runAt99 = Passing() with { Fmr = QualificationBoundedRate.Of(0, 40_000, 0.99) };
        var targets = QualificationTargets.Parse("""{ "fmrMax": 0.0001, "confidence": 0.95, "apcerUpperMax": 0.01, "minAttacksPerClass": 300, "automationTarget": 425, "automationOf": 500, "automationWilsonLowMin": 0.8, "identifyingFieldExactnessMin": 0.98, "criticalFalseApprovalsMax": 0 }""");

        var report = CandidateVersionReport.Build(Results(runAt99), Frozen(), targets);

        // At 99% the upper bound of 0 in 40,000 is above 1 in 10,000; the report judges at the 95% of the targets.
        Assert.True(IdentityStatistics.ClopperPearsonUpper(0, 40_000, 0.99) > 0.0001);
        Assert.True(report.Verdicts.Single(verdict => verdict.Target == "fmr").Pass);
        Assert.Contains("upper bound 0.000075", report.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_EndsWithItsOwnSha256_TheSameForTheSameInput_AndDifferentWhenTheInputIs()
    {
        var report = Report(Passing());
        var again = Report(Passing());
        var other = Report(Passing() with { Bpcer = Bounded(11, 1_000) });

        // The last line is the hash of every line above it, as UTF-8 with \n line endings: what a platform administrator gives to the qualification of the segments.
        var lines = report.Markdown.TrimEnd('\n').Split('\n');
        var body = string.Join('\n', lines[..^1]) + "\n";
        Assert.Equal(CandidateVersionReport.HashLinePrefix + report.Sha256, lines[^1]);
        Assert.Equal(Sha256(body), report.Sha256);
        Assert.Equal(64, report.Sha256.Length);
        Assert.True(report.Markdown.EndsWith('\n'));

        Assert.Equal(report.Markdown, again.Markdown);
        Assert.Equal(report.Sha256, again.Sha256);
        Assert.Equal(report.ToJson(), again.ToJson());
        Assert.NotEqual(report.Sha256, other.Sha256);

        // The data form carries the same hash; and the hash is checked against the text, so a changed report is told.
        Assert.Contains($"\"reportSha256\": \"{report.Sha256}\"", report.ToJson(), StringComparison.Ordinal);
        Assert.Equal(report.Sha256, CandidateVersionReport.ReadHash(report.Markdown));
        Assert.True(CandidateVersionReport.VerifyHash(report.Markdown));
        Assert.False(CandidateVersionReport.VerifyHash(report.Markdown.Replace("0/40000", "0/40001", StringComparison.Ordinal)));
        Assert.False(CandidateVersionReport.VerifyHash("no hash here\n"));
        Assert.Null(CandidateVersionReport.ReadHash(string.Empty));
    }

    /// <summary>Proof (privacy, ADR-013-R2): the report is an aggregate; no capture, no person and no path of the run is in it.</summary>
    [Fact]
    public void Report_HoldsNoCaseOfTheRun_OnlyAggregates()
    {
        QualificationFieldResult Field(string name) => new(name, 0, 1);
        var cases = new List<QualificationCaseResult>
        {
            QualificationTestData.Case("capture-SENTINEL-0001", QualificationCaseKinds.Genuine, QualificationTestData.Document(true, "passed", Field("surname")), QualificationTestData.Presence(true, 0.9, 0.9), QualificationTestData.Decision("auto_approved", "approve")),
            QualificationTestData.Case("capture-SENTINEL-0002", QualificationCaseKinds.Attack, null, QualificationTestData.Presence(false), null, attackClass: QualificationAttackClasses.Synthetic),
        };

        var report = Report(Passing(), cases: cases);

        Assert.DoesNotContain("SENTINEL", report.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("capture-", report.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_ARunThatIsNotOfTheFrozenConfiguration_SaysSo_AndNamesWhatDiffers()
    {
        var report = Report(Passing(), Frozen(weightsHash: new string('9', 64)));

        Assert.Contains("THE RUN DOES NOT USE THE CONFIGURATION OF THE LOCK, it differs in: weightsHash.", report.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Frozen configuration: the run used exactly", report.Markdown, StringComparison.Ordinal);

        // Only the names of the members are said, and the data form says the same.
        Assert.Equal(["weightsHash"], JsonDocument.Parse(report.ToJson()).RootElement.GetProperty("runDiffersFromLock").EnumerateArray().Select(member => member.GetString()));
    }

    [Fact]
    public void Report_ACandidateModel_IsComparedAtTheSameAcceptances()
    {
        var comparison = QualificationMetrics.CompareAtTheSameAcceptances(
            "match",
            "SFace 2021dec",
            "Other 1",
            0.85,
            [(0.9, 0.95), (0.8, 0.97), (0.6, 0.90)],
            [(0.9, 0.50), (0.3, 0.40), (0.2, 0.10)])!;

        var report = Report(Passing() with { CandidateComparisons = [comparison] });

        Assert.Contains("## Candidate models", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("- match: reference SFace 2021dec, candidate Other 1.", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("  - reference at 0.85: 1 of 3 impostor comparisons accepted, 2 of 3 genuine comparisons rejected.", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("  - candidate at 0.4: 1 of 3 impostor comparisons accepted, 0 of 3 genuine comparisons rejected.", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("  - the candidate rejects fewer genuine comparisons at the same limit.", report.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Candidate models", Report(Passing()).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_CreatesTheMarkdownAndTheDataOfTheReport_AndNeverReplacesThem()
    {
        var report = Report(Passing());

        report.Write(_folder);

        Assert.Equal(report.Markdown, File.ReadAllText(Path.Combine(_folder, CandidateVersionReport.MarkdownFileName)));
        Assert.Equal(report.ToJson(), File.ReadAllText(Path.Combine(_folder, CandidateVersionReport.JsonFileName)));
        Assert.True(CandidateVersionReport.ExistsIn(_folder));
        Assert.True(CandidateVersionReport.VerifyHash(File.ReadAllText(Path.Combine(_folder, CandidateVersionReport.MarkdownFileName))));

        // The report of a final test is written once.
        var again = Assert.Throws<QualificationRunException>(() => Report(Passing() with { Bpcer = Bounded(11, 1_000) }).Write(_folder));
        Assert.Equal(QualificationRunCodes.FinalAlreadyRun, again.Code);
        Assert.Equal(report.Markdown, File.ReadAllText(Path.Combine(_folder, CandidateVersionReport.MarkdownFileName)));
    }
}
