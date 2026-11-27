using System.Text.Json;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Qualification;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F01/US02/T02 (NF-198, D12): the frozen thresholds and aggregation. What a qualification freezes (the policy, the weights, the aggregation, the thresholds, the dataset) goes into a lock,
/// and the final test starts only when what is in force is that lock: each member that differs makes it refuse, naming the member and never its value, and a provisional policy is never
/// frozen and never passes. These tests need no model and no dataset.
/// </summary>
public sealed class QualificationLockTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "casazen-qualification-lock-" + Guid.NewGuid().ToString("N"));

    public QualificationLockTests()
    {
        Directory.CreateDirectory(_folder);
    }

    // The lock of the gate tests: next to it the final test writes its marker (the file itself is never written by a gate test).
    private string LockPath => Path.Combine(_folder, "lock", QualificationLock.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static QualificationLock Frozen(bool candidate = false, bool provisional = false)
    {
        return QualificationLock.From(
            QualificationTestData.Calibrated() with { Provisional = provisional },
            QualificationTestData.PolicyHash.ToUpperInvariant(),
            QualificationTestData.Snapshot(candidate),
            QualificationTestData.ManifestHash,
            120,
            QualificationTestData.RunAt);
    }

    [Fact]
    public void From_TheConfigurationInForce_IsDescribedByEveryMemberTheBriefNames()
    {
        var frozen = Frozen(candidate: true);

        Assert.Equal(("identity-policy-2026.11-1", QualificationTestData.PolicyHash, false), (frozen.PolicyVersion, frozen.PolicyCanonicalHash, frozen.PolicyProvisional));
        Assert.Equal(IdentityConfigurationFingerprint.ComputeWeightsHash(QualificationTestData.Snapshot()), frozen.WeightsHash);
        Assert.Equal(
            (QualificationTestData.DetectorHash, QualificationTestData.AntiSpoofingHash, QualificationTestData.RecognitionHash),
            (frozen.Weights.FaceDetector, frozen.Weights.AntiSpoofing, frozen.Weights.FaceRecognition));
        Assert.Equal(QualificationTestData.CandidateHash, Assert.Single(frozen.Candidates).Value);
        Assert.Equal(BiometricModelRoles.FaceRecognitionCandidate, Assert.Single(frozen.Candidates).Key);
        Assert.Equal(("median", 3, "all", 5), (frozen.Aggregation.MatchRule, frozen.Aggregation.MatchFrames, frozen.Aggregation.LivenessRule, frozen.Aggregation.LivenessFrames));
        Assert.Equal((0.85, 0.7, 0.7, 0.7), (frozen.Thresholds.Accept, frozen.Thresholds.ReviewFloor, frozen.Thresholds.MinEachFrame, frozen.Thresholds.LivenessMinScore));
        Assert.Equal((QualificationTestData.ManifestHash, 120, QualificationTestData.RunAt), (frozen.DatasetManifestHash, frozen.TestPersons, frozen.CreatedAt));
        Assert.Empty(Frozen().Candidates);
    }

    [Fact]
    public void Verify_TheSameConfigurationInForce_Holds_WhateverTheDayOfTheLock()
    {
        var current = Frozen() with { CreatedAt = QualificationTestData.RunAt.AddDays(30) };

        var verdict = Frozen().Verify(current);

        Assert.True(verdict.Ok);
        Assert.Null(verdict.Code);
        Assert.Empty(verdict.DifferingMembers);
        verdict.ThrowIfMismatch();
    }

    [Fact]
    public void Verify_EachMemberThatDiffers_IsNamedAndNothingElse()
    {
        var frozen = Frozen();
        var withCandidate = Frozen(candidate: true);
        var other = new string('9', 64);
        var changes = new Dictionary<string, QualificationLock>(StringComparer.Ordinal)
        {
            ["policyVersion"] = frozen with { PolicyVersion = "identity-policy-2026.12-1" },
            ["policyCanonicalHash"] = frozen with { PolicyCanonicalHash = other },
            ["policyProvisional"] = frozen with { PolicyProvisional = true },
            ["weightsHash"] = frozen with { WeightsHash = other },
            ["weights.faceDetector"] = frozen with { Weights = frozen.Weights with { FaceDetector = other } },
            ["weights.antiSpoofing"] = frozen with { Weights = frozen.Weights with { AntiSpoofing = other } },
            ["weights.faceRecognition"] = frozen with { Weights = frozen.Weights with { FaceRecognition = other } },
            ["candidates." + BiometricModelRoles.FaceRecognitionCandidate] = withCandidate,
            ["aggregation.matchRule"] = frozen with { Aggregation = frozen.Aggregation with { MatchRule = "mean" } },
            ["aggregation.matchFrames"] = frozen with { Aggregation = frozen.Aggregation with { MatchFrames = 4 } },
            ["aggregation.livenessRule"] = frozen with { Aggregation = frozen.Aggregation with { LivenessRule = "any" } },
            ["aggregation.livenessFrames"] = frozen with { Aggregation = frozen.Aggregation with { LivenessFrames = 6 } },
            ["thresholds.accept"] = frozen with { Thresholds = frozen.Thresholds with { Accept = 0.84 } },
            ["thresholds.reviewFloor"] = frozen with { Thresholds = frozen.Thresholds with { ReviewFloor = 0.69 } },
            ["thresholds.minEachFrame"] = frozen with { Thresholds = frozen.Thresholds with { MinEachFrame = 0.69 } },
            ["thresholds.livenessMinScore"] = frozen with { Thresholds = frozen.Thresholds with { LivenessMinScore = 0.69 } },
            ["datasetManifestHash"] = frozen with { DatasetManifestHash = other },
            ["testPersons"] = frozen with { TestPersons = 121 },
        };

        foreach (var (member, current) in changes)
            Assert.Equal([member], frozen.Verify(current).DifferingMembers);

        // The other way round too: a candidate that was frozen and is not loaded now.
        Assert.Equal(["candidates." + BiometricModelRoles.FaceRecognitionCandidate], withCandidate.Verify(frozen).DifferingMembers);

        // Several at once are all named, in the order of the lock.
        Assert.Equal(
            ["policyCanonicalHash", "thresholds.accept", "testPersons"],
            frozen.Verify(frozen with { PolicyCanonicalHash = other, Thresholds = frozen.Thresholds with { Accept = 0.5 }, TestPersons = 3 }).DifferingMembers);
    }

    [Fact]
    public void Verify_ARefusalNamesTheMembersAndNeverTheirValues()
    {
        var frozen = Frozen();
        var changed = frozen with { PolicyCanonicalHash = new string('7', 64), Thresholds = frozen.Thresholds with { Accept = 0.8317 } };

        var refusal = Assert.Throws<QualificationLockMismatchException>(() => frozen.Verify(changed).ThrowIfMismatch());

        Assert.Equal("qualification_lock_mismatch", refusal.Code);
        Assert.Equal("qualification_lock_mismatch: policyCanonicalHash, thresholds.accept", refusal.Message);
        Assert.DoesNotContain("0.8317", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('7', 8), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_AProvisionalPolicy_NeverPasses_InForceOrInTheLock()
    {
        var provisionalNow = Frozen(provisional: true);

        // Whatever else is equal, a policy that is provisional is a difference: it is never qualified.
        Assert.Equal(["policyProvisional"], Frozen().Verify(provisionalNow).DifferingMembers);
        Assert.Equal(["policyProvisional"], provisionalNow.Verify(provisionalNow).DifferingMembers);
        Assert.Equal(QualificationLock.MismatchCode, provisionalNow.Verify(provisionalNow).Code);
    }

    /// <summary>Proof (security): the thresholds were frozen before the final test, so a threshold changed after the calibration makes the final test refuse to start, before anything of the test split is read.</summary>
    [Fact]
    public async Task QualificationLock_ChangedThreshold_RefusesFinalTest()
    {
        var frozen = Frozen();
        var current = frozen with { Thresholds = frozen.Thresholds with { Accept = 0.60 } };
        var started = 0;

        var refusal = await Assert.ThrowsAsync<QualificationLockMismatchException>(() => QualificationFinalGate.RunOnceAsync(frozen, current, _folder, LockPath, () =>
        {
            started++;
            return Task.FromResult(1);
        }));

        Assert.Equal("qualification_lock_mismatch", refusal.Code);
        Assert.Equal(["thresholds.accept"], refusal.Members);
        Assert.Equal(0, started);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public async Task QualificationFinalGate_APolicyThatIsProvisionalOrAWeightThatChanged_RefusesToStart()
    {
        var frozen = Frozen();
        var started = 0;
        Task<int> Run()
        {
            started++;
            return Task.FromResult(1);
        }

        var provisional = await Assert.ThrowsAsync<QualificationLockMismatchException>(() => QualificationFinalGate.RunOnceAsync(frozen, Frozen(provisional: true), _folder, LockPath, Run));
        var weights = await Assert.ThrowsAsync<QualificationLockMismatchException>(() => QualificationFinalGate.RunOnceAsync(frozen, frozen with { Weights = frozen.Weights with { FaceRecognition = new string('8', 64) } }, _folder, LockPath, Run));
        var manifest = await Assert.ThrowsAsync<QualificationLockMismatchException>(() => QualificationFinalGate.RunOnceAsync(frozen, frozen with { DatasetManifestHash = new string('6', 64) }, _folder, LockPath, Run));
        var aggregation = await Assert.ThrowsAsync<QualificationLockMismatchException>(() => QualificationFinalGate.RunOnceAsync(frozen, frozen with { Aggregation = frozen.Aggregation with { MatchRule = "min" } }, _folder, LockPath, Run));

        Assert.Equal(["policyProvisional"], provisional.Members);
        Assert.Equal(["weights.faceRecognition"], weights.Members);
        Assert.Equal(["datasetManifestHash"], manifest.Members);
        Assert.Equal(["aggregation.matchRule"], aggregation.Members);
        Assert.Equal(0, started);
        Assert.False(File.Exists(QualificationFinalGate.MarkerPathOf(LockPath)));
    }

    [Fact]
    public async Task QualificationFinalGate_TheFrozenConfiguration_StartsTheRunOnce_AndAReportInTheFolderRefusesAnotherRun()
    {
        var frozen = Frozen();
        var started = 0;
        Task<string> Run()
        {
            started++;
            return Task.FromResult("results");
        }

        var results = await QualificationFinalGate.RunOnceAsync(frozen, Frozen(), _folder, LockPath, Run);
        Assert.Equal(("results", 1), (results, started));

        // The final test is run once: with the report of one in the folder, nothing starts again, and nothing replaces that report.
        File.WriteAllText(Path.Combine(_folder, CandidateVersionReport.MarkdownFileName), "the report of the final test");
        var again = await Assert.ThrowsAsync<QualificationRunException>(() => QualificationFinalGate.RunOnceAsync(frozen, Frozen(), _folder, LockPath, Run));

        Assert.Equal(QualificationRunCodes.FinalAlreadyRun, again.Code);
        Assert.Equal(1, started);
        Assert.Equal("the report of the final test", File.ReadAllText(Path.Combine(_folder, CandidateVersionReport.MarkdownFileName)));
    }

    /// <summary>
    /// Proof (security): «once» holds whatever the output folder is and whatever is deleted. The marker is written next to the lock BEFORE the run starts (so before anything of the test split is
    /// read), is never replaced, and a second final test with the same lock is refused in another folder, with the report deleted, or after a run that broke.
    /// </summary>
    [Fact]
    public async Task QualificationFinalGate_TheMarkerIsWrittenBeforeTheRun_AndRefusesAnotherRunWhateverTheFolder()
    {
        var frozen = Frozen();
        var marker = QualificationFinalGate.MarkerPathOf(LockPath);
        var started = 0;
        var markerWhenTheRunStarted = false;
        Task<int> Run()
        {
            started++;
            markerWhenTheRunStarted = File.Exists(marker);
            return Task.FromResult(1);
        }

        await QualificationFinalGate.RunOnceAsync(frozen, Frozen(), Path.Combine(_folder, "out-1"), LockPath, Run);

        Assert.True(markerWhenTheRunStarted);
        Assert.Equal(Path.GetFullPath(LockPath) + QualificationFinalGate.MarkerSuffix, marker);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(LockPath)), Path.GetDirectoryName(marker));

        // Another output folder, the report that was never written, the same lock: refused, and nothing starts.
        var elsewhere = await Assert.ThrowsAsync<QualificationRunException>(() => QualificationFinalGate.RunOnceAsync(frozen, Frozen(), Path.Combine(_folder, "out-2"), LockPath, Run));
        Assert.Equal(QualificationRunCodes.FinalAlreadyRun, elsewhere.Code);
        Assert.Equal(1, started);

        // What the marker holds is hashes and the time: nothing of a person.
        using var content = JsonDocument.Parse(File.ReadAllText(marker));
        Assert.Equal(["datasetManifestHash", "policyCanonicalHash", "startedAt", "weightsHash"], content.RootElement.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.Equal(frozen.PolicyCanonicalHash, content.RootElement.GetProperty("policyCanonicalHash").GetString());
    }

    [Fact]
    public async Task QualificationFinalGate_ARunThatBreaks_LeavesTheMarker_SoTheTestIsNotRunAgainOnTheSameLock()
    {
        var frozen = Frozen();
        var started = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => QualificationFinalGate.RunOnceAsync<int>(frozen, Frozen(), _folder, LockPath, () =>
        {
            started++;
            throw new InvalidOperationException("the run broke");
        }));
        var again = await Assert.ThrowsAsync<QualificationRunException>(() => QualificationFinalGate.RunOnceAsync(frozen, Frozen(), _folder, LockPath, () =>
        {
            started++;
            return Task.FromResult(1);
        }));

        Assert.True(File.Exists(QualificationFinalGate.MarkerPathOf(LockPath)));
        Assert.Equal(QualificationRunCodes.FinalAlreadyRun, again.Code);
        Assert.Equal(1, started);
    }

    /// <summary>A test split that cannot measure a kind of critical case is refused before the marker is written: the final test is made once, and it must not be used up on a dataset that cannot pass.</summary>
    [Fact]
    public async Task QualificationFinalGate_ASplitWithoutAKindOfCriticalCase_IsRefusedBeforeTheMarker()
    {
        var frozen = Frozen();
        var started = 0;
        Task<int> Run()
        {
            started++;
            return Task.FromResult(1);
        }

        var refusal = await Assert.ThrowsAsync<QualificationRunException>(
            () => QualificationFinalGate.RunOnceAsync(frozen, Frozen(), _folder, LockPath, Run, [QualificationCriticalKinds.Altered, QualificationCriticalKinds.Foreign]));

        Assert.Equal(QualificationRunCodes.CriticalKindsMissing, refusal.Code);
        Assert.Contains("altered, foreign", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, started);
        Assert.False(File.Exists(QualificationFinalGate.MarkerPathOf(LockPath)));

        // With every kind there, the same lock runs.
        await QualificationFinalGate.RunOnceAsync(frozen, Frozen(), _folder, LockPath, Run, []);
        Assert.Equal(1, started);
    }

    [Fact]
    public void Save_APolicyThatIsProvisional_IsNeverFrozen_AndALockThatIsThereIsNeverReplaced()
    {
        var path = Path.Combine(_folder, QualificationLock.FileName);

        var provisional = Assert.Throws<QualificationRunException>(() => Frozen(provisional: true).Save(path));
        Assert.Equal(QualificationRunCodes.LockNotFreezable, provisional.Code);
        Assert.False(File.Exists(path));

        Frozen().Save(path);
        var written = File.ReadAllText(path);
        var replaced = Assert.Throws<QualificationRunException>(() => (Frozen() with { TestPersons = 1 }).Save(path));

        Assert.Equal(QualificationRunCodes.LockNotFreezable, replaced.Code);
        Assert.Equal(written, File.ReadAllText(path));
    }

    [Fact]
    public void Load_ALockWrittenToItsFile_ReadsBackAsItWas()
    {
        var path = Path.Combine(_folder, "nested", QualificationLock.FileName);
        var frozen = Frozen(candidate: true);

        frozen.Save(path);
        var read = QualificationLock.Load(path);

        Assert.Equal(frozen.ToJson(), read.ToJson());
        Assert.True(frozen.Verify(read).Ok);
        var json = File.ReadAllText(path);
        Assert.Contains("\"policyCanonicalHash\"", json, StringComparison.Ordinal);
        Assert.Contains("\"datasetManifestHash\"", json, StringComparison.Ordinal);
        Assert.Contains("\"testPersons\": 120", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AFileThatIsNotAWholeLock_IsUnreadable_WithoutQuotingIt()
    {
        var good = JsonDocument.Parse(Frozen().ToJson()).RootElement;
        string Without(string member) => "{" + string.Join(",", good.EnumerateObject().Where(property => property.Name != member).Select(property => $"\"{property.Name}\":{property.Value.GetRawText()}")) + "}";
        var texts = new[]
        {
            string.Empty,
            "null",
            "SECRET-TEXT",
            "{ \"secret\": \"SECRET-TEXT\" }",
            Without("weights"),
            Without("thresholds"),
            Without("datasetManifestHash"),
            Frozen().ToJson().Replace(QualificationTestData.PolicyHash, "not-a-hash", StringComparison.Ordinal),
            Frozen().ToJson().Replace("\"matchFrames\": 3", "\"matchFrames\": 0", StringComparison.Ordinal),
            Frozen().ToJson().Replace("\"accept\": 0.85", "\"accept\": 1.5", StringComparison.Ordinal),
            Frozen().ToJson().Replace("}", ", \"surprise\": 1 }", StringComparison.Ordinal),
        };

        for (var index = 0; index < texts.Length; index++)
        {
            var path = Path.Combine(_folder, $"lock-{index}.json");
            File.WriteAllText(path, texts[index]);

            var refusal = Assert.Throws<QualificationRunException>(() => QualificationLock.Load(path));

            Assert.Equal(QualificationRunCodes.LockUnreadable, refusal.Code);
            Assert.DoesNotContain("SECRET", refusal.Message, StringComparison.Ordinal);
        }

        Assert.Throws<QualificationRunException>(() => QualificationLock.Load(Path.Combine(_folder, "missing.json")));
    }

    [Fact]
    public void From_ModelsThatAreNotAllLoaded_CannotBeFrozen()
    {
        var detector = new BiometricModelInfo(BiometricModelRoles.FaceDetector, "YuNet", "1", "MIT", "p", null, false, "hash_mismatch");
        var broken = new BiometricModelsSnapshot(BiometricModelsState.Unavailable, "hash_mismatch", [detector]);

        var refusal = Assert.Throws<QualificationRunException>(() => QualificationLock.From(QualificationTestData.Calibrated(), QualificationTestData.PolicyHash, broken, QualificationTestData.ManifestHash, 1, QualificationTestData.RunAt));
        var hash = Assert.Throws<QualificationRunException>(() => QualificationLock.From(QualificationTestData.Calibrated(), "nope", QualificationTestData.Snapshot(), QualificationTestData.ManifestHash, 1, QualificationTestData.RunAt));

        Assert.Equal(QualificationRunCodes.LockNotFreezable, refusal.Code);
        Assert.Equal(QualificationRunCodes.LockNotFreezable, hash.Code);
    }

    [Fact]
    public void FromResults_TheConfigurationOfARun_IsTheLockItWasMadeWith()
    {
        var results = QualificationTestData.Results(QualificationSplits.Test, [], [], models: QualificationTestData.Models(candidate: true));

        var described = QualificationLock.FromResults(results);

        // The run of the committed policy is the lock of the same configuration (the test persons are the ones the results count in the dataset), and a run of anything else is named.
        Assert.True(Frozen(candidate: true).Verify(described).Ok);
        Assert.Equal(results.RunAt, described.CreatedAt);
        Assert.Equal(["candidates." + BiometricModelRoles.FaceRecognitionCandidate], Frozen().Verify(described).DifferingMembers);
    }
}
