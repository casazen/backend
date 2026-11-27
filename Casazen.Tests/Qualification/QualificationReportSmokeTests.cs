using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T02 (NF-198, D12): the lock and the report of the candidate version on the real pipeline, with the runs the other tests of the toolkit share (the collections keep them to one run
// each): what the application has in force is described by a lock, the published policy is provisional so that lock cannot be frozen, and the report of a run is whole, ends with its own
// SHA-256 and carries nothing of the ground truth. Generated data, no score of the models asserted.

/// <summary>The lock and the report of the run of the smoke test (see the comments at the top of the file).</summary>
[Collection(QualificationSmokeCollection.Name)]
public sealed class QualificationReportSmokeTests(QualificationSmokeFixture run)
{
    [Fact]
    public void QualificationSmoke_LockOfTheRealPipeline_IsWhole_AndAProvisionalPolicyIsNeverFrozen()
    {
        var (manifestHash, testPersons) = QualificationLock.DatasetIdentityOf(run.ManifestPath);

        var captured = QualificationLock.Capture(run.Pipeline, manifestHash, testPersons, run.Pipeline.Clock.GetUtcNow());

        // The published policy is provisional: its lock describes the configuration, is refused as a final-test gate (the member is named) and cannot be written.
        Assert.True(captured.PolicyProvisional);
        Assert.Equal(run.Results.DatasetManifestHash, captured.DatasetManifestHash);
        Assert.Equal(0, captured.TestPersons);
        Assert.Equal(["policyProvisional"], captured.Verify(captured).DifferingMembers);
        var refused = Assert.Throws<QualificationRunException>(() => captured.Save(Path.Combine(run.OutDirectory, QualificationLock.FileName)));
        Assert.Equal(QualificationRunCodes.LockNotFreezable, refused.Code);
        Assert.False(File.Exists(Path.Combine(run.OutDirectory, QualificationLock.FileName)));

        // What the results describe is what was captured: the same policy, the same weights, the same thresholds, the same dataset.
        Assert.DoesNotContain(captured.Verify(QualificationLock.FromResults(run.Results)).DifferingMembers, member => member is not ("policyProvisional" or "testPersons"));
    }

    [Fact]
    public void QualificationSmoke_ReportOfTheRun_EndsWithItsOwnSha256_AndCarriesNoGroundTruth()
    {
        var (manifestHash, testPersons) = QualificationLock.DatasetIdentityOf(run.ManifestPath);
        var captured = QualificationLock.Capture(run.Pipeline, manifestHash, testPersons, run.Pipeline.Clock.GetUtcNow());

        var report = CandidateVersionReport.Build(run.Results, captured, QualificationTargets.Load());

        Assert.True(CandidateVersionReport.VerifyHash(report.Markdown));
        Assert.Equal(report.Sha256, CandidateVersionReport.ReadHash(report.Markdown));
        Assert.Contains(captured.WeightsHash, report.Markdown, StringComparison.Ordinal);
        Assert.Contains(run.Results.Policy.CanonicalHash, report.Markdown, StringComparison.Ordinal);
        Assert.Contains("it differs in: policyProvisional.", report.Markdown, StringComparison.Ordinal);
        Assert.Empty(QualificationSmokeFixture.LeaksIn(report.Markdown));
        Assert.Empty(QualificationSmokeFixture.LeaksIn(report.ToJson()));

        // Generated pictures are not faces and the dataset is two persons: no target can be shown, and the report says so for every verdict it could not make.
        Assert.False(report.AllTargetsPassed);
    }
}

/// <summary>The lock and the report of the run of the logic test, which has a candidate of each model loaded (see the comments at the top of the file).</summary>
[Collection(QualificationLogicCollection.Name)]
public sealed class QualificationReportLogicTests(QualificationLogicFixture run)
{
    [Fact]
    public void Run_TheLockOfTheRun_HoldsTheCandidatesThatWereLoaded_AndTheReportOfTheRunIsWhole()
    {
        var (manifestHash, _) = QualificationLock.DatasetIdentityOf(run.ManifestPath);
        var captured = QualificationLock.Capture(run.Pipeline, manifestHash, 0, run.Pipeline.Clock.GetUtcNow());

        Assert.Equal([BiometricModelRoles.AntiSpoofingCandidate, BiometricModelRoles.FaceRecognitionCandidate], captured.Candidates.Keys.Order(StringComparer.Ordinal));
        var report = CandidateVersionReport.Build(run.Results, captured, QualificationTargets.Load());

        Assert.True(CandidateVersionReport.VerifyHash(report.Markdown));
        Assert.Contains("## Candidate models", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("it differs in: policyProvisional.", report.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("ZQ", report.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("- match: reference", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("- liveness: reference", report.Markdown, StringComparison.Ordinal);
    }
}
