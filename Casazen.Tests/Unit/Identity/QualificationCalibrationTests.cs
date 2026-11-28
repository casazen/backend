using System.Text.Json;
using Casazen.Core.Identity.Statistics;
using Casazen.Core.Options;
using Casazen.Infrastructure.Identity;
using Casazen.Tests.Qualification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F01/US02/T02 (NF-198, D12): the calibration output. From the results of the calibration split the toolkit proposes the thresholds (the acceptance at the target false match rate, the floor of
/// the review, the least liveness at the target rate of attacks accepted) as a NEW policy file, not provisional, in the output folder, and never edits a committed policy. These tests work on scores
/// written in code: no model and no dataset.
/// </summary>
public sealed class QualificationCalibrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "casazen-qualification-calibration-" + Guid.NewGuid().ToString("N"));
    private readonly QualificationTargets _targets = QualificationTargets.Load();
    private readonly TimeProvider _clock = new FixedTimeProvider(new DateTimeOffset(2026, 12, 5, 12, 0, 0, TimeSpan.Zero));

    public QualificationCalibrationTests()
    {
        Directory.CreateDirectory(Committed);
    }

    private string Committed => Path.Combine(_folder, "committed");

    private string Out => Path.Combine(_folder, "out");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static readonly Dictionary<string, double> LivenessBase = new(StringComparer.Ordinal)
    {
        [QualificationAttackClasses.PhotoPrint] = 0.05,
        [QualificationAttackClasses.ScreenReplay] = 0.10,
        [QualificationAttackClasses.VideoReplay] = 0.20,
        [QualificationAttackClasses.Synthetic] = 0.01,
    };

    /// <summary>
    /// Results of a calibration run written in code: <paramref name="impostors"/> trials with similarities from 0.30 to 0.7995 (a frame never under its minimum), <paramref name="genuine"/>
    /// genuine cases from 0.55 to 0.8965, and <paramref name="attacksPerClass"/> attacks of each class the specification names, valid, chosen and judged, with the lowest liveness of a class
    /// reaching 0.299 above its base.
    /// </summary>
    private static QualificationResults Results(
        string split = QualificationSplits.Calibration, int impostors = 40_000, int genuine = 100, int attacksPerClass = 300, double impostorMin = 0.95, bool attacksReachLiveness = true)
    {
        var trials = Enumerable.Range(0, impostors)
            .Select(index => new QualificationImpostorTrial($"g-{index % 50}", $"d-{index}", 0.3 + ((index % 1000) * 0.0005), impostorMin, 10, null))
            .ToList<QualificationImpostorTrial>();
        var cases = new List<QualificationCaseResult>();
        for (var index = 0; index < genuine; index++)
        {
            var similarity = 0.55 + (index * 0.0035);
            cases.Add(QualificationTestData.Case($"genuine-{index}", QualificationCaseKinds.Genuine, QualificationTestData.Document(true), QualificationTestData.Presence(true, similarity, similarity)));
        }

        foreach (var (attackClass, baseScore) in LivenessBase)
        {
            for (var index = 0; index < attacksPerClass; index++)
            {
                var lowest = baseScore + (index * 0.001);
                cases.Add(QualificationTestData.Case(
                    $"{attackClass}-{index}",
                    QualificationCaseKinds.Attack,
                    null,
                    QualificationTestData.Presence(false, liveness: [lowest, lowest + 0.2, lowest + 0.3], validation: attacksReachLiveness),
                    attackClass: attackClass));
            }
        }

        return QualificationTestData.Results(split, cases, trials);
    }

    private QualificationCalibrationProposal Propose(QualificationResults results, IdentityDecisionPolicy? active = null)
    {
        return QualificationCalibration.Propose(results, _targets, active ?? IdentityTestPolicy.Shipped(), [Committed], Out, _clock);
    }

    private static string[] PolicyFilesIn(string folder)
    {
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "identity-policy-*.json").Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)] : [];
    }

    [Fact]
    public void Propose_FromTheCalibrationSplit_WritesAPolicyThatIsNotProvisionalWithTheThresholdsOfTheTargets()
    {
        var proposal = Propose(Results());

        Assert.True(proposal.Proposed);
        Assert.Empty(proposal.Codes);
        Assert.Equal("identity-policy-2026.12-1", proposal.PolicyVersion);
        Assert.Equal(["identity-policy-2026.12-1.json"], PolicyFilesIn(Out));
        Assert.Equal("identity-policy-2026.12-1.json", proposal.PolicyFile);

        var thresholds = proposal.Thresholds!;
        Assert.Equal(Math.BitIncrement(0.3 + (999 * 0.0005)), thresholds.Accept);
        Assert.Equal(0.55 + (4 * 0.0035), thresholds.ReviewFloor, 9);
        Assert.Equal(0.7, thresholds.MinEachFrame);
        Assert.Equal(Math.BitIncrement(0.20 + (299 * 0.001)), thresholds.LivenessMinScore);
        Assert.Equal(["version", "provisional", "match.accept", "match.reviewFloor", "presence.liveness.minScore"], proposal.ChangedMembers);
        Assert.Equal((40_000L, 100L), (proposal.Evidence.ImpostorComparisons, proposal.Evidence.GenuineComparisons));
        Assert.Equal(300, proposal.Evidence.AttacksByClass[QualificationAttackClasses.PhotoPrint]);
        Assert.Equal(300, proposal.Evidence.AttacksReachingLiveness[QualificationAttackClasses.Synthetic]);

        Assert.True(IdentityStatistics.ClopperPearsonUpper(0, 40_000) <= _targets.FmrMax);
        Assert.True(IdentityStatistics.ClopperPearsonUpper(1, 40_000) > _targets.FmrMax);
    }

    [Fact]
    public void Propose_TheFileIsOneTheApplicationReads_PinnedByItsCanonicalHash_AndOnlyTheThresholdsChanged()
    {
        var active = IdentityTestPolicy.Shipped();

        var proposal = Propose(Results(), active);

        var path = Path.Combine(Out, proposal.PolicyFile!);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(IdentityPolicyProvider.CanonicalSha256(bytes), proposal.PolicyCanonicalHash);

        var options = Options.Create(new IdentityVerificationOptions { PolicyFile = path, PolicySha256 = proposal.PolicyCanonicalHash! });
        var snapshot = new IdentityPolicyProvider(options, NullLogger<IdentityPolicyProvider>.Instance, Out).Snapshot;
        Assert.True(snapshot.IsReady);
        Assert.False(snapshot.Provisional);
        var read = snapshot.Policy!;
        Assert.Equal(("identity-policy-2026.12-1", false), (read.Version, read.Provisional));
        Assert.Equal((proposal.Thresholds!.Accept, proposal.Thresholds.ReviewFloor), (read.Match.Accept, read.Match.ReviewFloor));
        Assert.Equal(active.Document.IdentifyingFields, read.Document.IdentifyingFields);
        Assert.Equal((active.Match.Frames, active.Match.Aggregation, active.Match.MinFrameGapMs, active.Match.RandomizeAmongTop), (read.Match.Frames, read.Match.Aggregation, read.Match.MinFrameGapMs, read.Match.RandomizeAmongTop));
        Assert.Equal(active.Presence.Challenge.Actions, read.Presence.Challenge.Actions);
        Assert.Equal((active.Presence.Pose, active.Presence.Continuity, active.Presence.Sequence), (read.Presence.Pose, read.Presence.Continuity, read.Presence.Sequence));
        Assert.Equal((active.Presence.Liveness.Frames, active.Presence.Liveness.Rule), (read.Presence.Liveness.Frames, read.Presence.Liveness.Rule));
        Assert.Equal(active.Closure, read.Closure);
        Assert.Equal(active.CaptureRules, read.CaptureRules);

        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Out, QualificationCalibration.ProposalFileName)));
        Assert.True(summary.RootElement.GetProperty("proposed").GetBoolean());
        Assert.Equal(proposal.PolicyCanonicalHash, summary.RootElement.GetProperty("policyCanonicalHash").GetString());
    }

    [Fact]
    public void Propose_NeverWritesToTheFolderOfTheCommittedPolicies_NorReplacesAFile()
    {
        var committedFile = Path.Combine(Committed, "identity-policy-2026.11-1.json");
        File.WriteAllText(committedFile, "THE COMMITTED POLICY");

        foreach (var spelled in new[] { Committed, Committed + Path.DirectorySeparatorChar, Path.Combine(Committed, "..", "committed"), Path.Combine(Committed, "proposals") })
        {
            var refusal = Assert.Throws<QualificationRunException>(() => QualificationCalibration.Propose(Results(), _targets, IdentityTestPolicy.Shipped(), [Committed], spelled, _clock));
            Assert.Equal(QualificationRunCodes.OutputFolderInvalid, refusal.Code);
        }

        Assert.Equal("THE COMMITTED POLICY", File.ReadAllText(committedFile));
        Assert.Equal(["identity-policy-2026.11-1.json"], PolicyFilesIn(Committed));
        Assert.Empty(Directory.EnumerateFiles(Committed, "calibration-proposal.json"));

        Assert.True(Propose(Results()).Proposed);
        Assert.Equal("THE COMMITTED POLICY", File.ReadAllText(committedFile));
        Assert.Equal(["identity-policy-2026.12-1.json"], PolicyFilesIn(Out));
    }

    [Fact]
    public void Propose_NumbersTheVersionAfterTheOnesOfTheMonthThatExist_AndLeavesThemAsTheyAre()
    {
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "identity-policy-2026.12-1.json"), "an earlier proposal");
        File.WriteAllText(Path.Combine(Committed, "identity-policy-2026.12-3.json"), "a committed policy");
        File.WriteAllText(Path.Combine(Committed, "identity-policy-2026.11-9.json"), "another month");

        var proposal = Propose(Results());

        Assert.Equal("identity-policy-2026.12-4", proposal.PolicyVersion);
        Assert.Equal(["identity-policy-2026.12-1.json", "identity-policy-2026.12-4.json"], PolicyFilesIn(Out));
        Assert.Equal("an earlier proposal", File.ReadAllText(Path.Combine(Out, "identity-policy-2026.12-1.json")));
        Assert.Equal("a committed policy", File.ReadAllText(Path.Combine(Committed, "identity-policy-2026.12-3.json")));
    }

    [Fact]
    public void Propose_WithTooLittleEvidence_GivesTheCodesOfWhatIsMissing_AndNoPolicy()
    {
        var proposal = Propose(Results(impostors: 1_000, genuine: 0, attacksPerClass: 100));

        Assert.False(proposal.Proposed);
        Assert.Null(proposal.PolicyFile);
        Assert.Null(proposal.Thresholds);
        Assert.Contains(QualificationCalibrationCodes.ImpostorTrialsInsufficient, proposal.Codes);
        Assert.Contains(QualificationCalibrationCodes.GenuineTrialsMissing, proposal.Codes);
        foreach (var attackClass in QualificationMetrics.CoreAttackClasses)
            Assert.Contains(QualificationCalibrationCodes.AttacksInsufficient + ":" + attackClass, proposal.Codes);

        Assert.Empty(PolicyFilesIn(Out));
        Assert.True(File.Exists(Path.Combine(Out, QualificationCalibration.ProposalFileName)));
    }

    [Fact]
    public void Propose_AClassTheSpecificationNamesThatWasNotTried_IsMissingEvidence()
    {
        var results = Results(attacksPerClass: 300);
        var without = QualificationTestData.Results(
            QualificationSplits.Calibration,
            [.. results.Cases.Where(candidate => candidate.AttackClass != QualificationAttackClasses.VideoReplay)],
            results.ImpostorTrials);

        var proposal = Propose(without);

        Assert.False(proposal.Proposed);
        Assert.Equal([QualificationCalibrationCodes.AttacksInsufficient + ":" + QualificationAttackClasses.VideoReplay], proposal.Codes);
    }

    [Fact]
    public void Propose_AnAttackThatCouldNotBeJudged_IsNoEvidence_AndIsCountedApart()
    {
        var results = Results(attacksPerClass: 300);
        var broken = 0;
        var cases = results.Cases.Select(candidate =>
        {
            if (candidate.AttackClass != QualificationAttackClasses.PhotoPrint || broken >= 100)
                return candidate;

            broken++;
            return broken % 3 == 0
                ? candidate with { Error = "InvalidOperationException" }
                : candidate with { Presence = candidate.Presence! with { ValidationUnavailable = broken % 3 == 1, IntakeError = broken % 3 == 2 ? "presence_sequence_invalid" : null } };
        }).ToList();

        var proposal = Propose(QualificationTestData.Results(QualificationSplits.Calibration, cases, results.ImpostorTrials));

        Assert.False(proposal.Proposed);
        Assert.Equal([QualificationCalibrationCodes.AttacksInsufficient + ":" + QualificationAttackClasses.PhotoPrint], proposal.Codes);
        Assert.Equal(200, proposal.Evidence.AttacksByClass[QualificationAttackClasses.PhotoPrint]);
        Assert.Equal(100, proposal.Evidence.AttacksNotJudged[QualificationAttackClasses.PhotoPrint]);
        Assert.Equal(300, proposal.Evidence.AttacksByClass[QualificationAttackClasses.Synthetic]);
        Assert.False(proposal.Evidence.AttacksNotJudged.ContainsKey(QualificationAttackClasses.Synthetic));
        Assert.Empty(PolicyFilesIn(Out));
    }

    [Theory]
    [InlineData(QualificationSplits.Test)]
    [InlineData(QualificationSplits.Development)]
    public void Propose_OnlyTheCalibrationSplitCalibrates(string split)
    {
        var proposal = Propose(Results(split));

        Assert.False(proposal.Proposed);
        Assert.Equal([QualificationCalibrationCodes.WrongSplit], proposal.Codes);
        Assert.Empty(PolicyFilesIn(Out));
    }

    [Fact]
    public void Propose_ImpostorsKeptOutByTheMinimumOfEachFrame_LeaveTheActiveAcceptanceAsItIs()
    {
        var proposal = Propose(Results(impostorMin: 0.2));

        Assert.True(proposal.Proposed);
        Assert.Equal(IdentityTestPolicy.Shipped().Match.Accept, proposal.Thresholds!.Accept);
        Assert.DoesNotContain("match.accept", proposal.ChangedMembers);
    }

    [Fact]
    public void Propose_AttacksThatNeverReachTheLiveness_LeaveTheActiveMinimumAsItIs()
    {
        var proposal = Propose(Results(attacksReachLiveness: false));

        Assert.True(proposal.Proposed);
        Assert.Equal(IdentityTestPolicy.Shipped().Presence.Liveness.MinScore, proposal.Thresholds!.LivenessMinScore);
        Assert.DoesNotContain("presence.liveness.minScore", proposal.ChangedMembers);
        Assert.All(proposal.Evidence.AttacksReachingLiveness.Values, count => Assert.Equal(0, count));
    }

    [Theory]
    [InlineData(30_000L, 0L)]
    [InlineData(29_900L, -1L)]
    [InlineData(300L, 0L)]
    [InlineData(299L, 0L)]
    [InlineData(298L, -1L)]
    [InlineData(0L, -1L)]
    public void MaxErrorsAllowed_IsTheMostErrorsWhoseUpperBoundIsStillUnderTheTarget(long trials, long expected)
    {
        var target = trials >= 1_000 || trials == 0 ? _targets.FmrMax : _targets.ApcerUpperMax;

        Assert.Equal(expected, QualificationCalibration.MaxErrorsAllowed(trials, target, _targets.Confidence));
    }

    [Fact]
    public void MaxErrorsAllowed_WithManyTrials_IsTheLargestNumberUnderTheBound_AndOneMoreIsNot()
    {
        var allowed = QualificationCalibration.MaxErrorsAllowed(100_000, 0.001, 0.95);

        Assert.True(allowed >= 1);
        Assert.True(IdentityStatistics.ClopperPearsonUpper(allowed, 100_000) <= 0.001);
        Assert.True(IdentityStatistics.ClopperPearsonUpper(allowed + 1, 100_000) > 0.001);
    }
}
