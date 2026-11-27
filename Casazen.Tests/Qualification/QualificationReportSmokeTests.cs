using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
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

        // The generated dataset has no person with the document of another and no altered or foreign document: the criterion on critical false approvals is not measured, and fails.
        var critical = report.Verdicts.Single(verdict => verdict.Target == "critical_false_approvals");
        Assert.False(critical.Pass);
        Assert.StartsWith("not measured: no case was decided for ", critical.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preflight of the final test reads the manifest and the counts of a split without opening a capture: the final test asks it before it writes its marker, so that a manifest that is wrong
    /// (or a split with nobody in it) does not use up the one run, and says which kinds of critical case the split cannot measure.
    /// </summary>
    [Fact]
    public void QualificationSmoke_Preflight_ReadsTheManifestAndTheSplit_WithoutOpeningACapture()
    {
        var preflight = QualificationRunner.Preflight(run.ManifestPath, QualificationSplits.Development);

        Assert.Equal(run.Results.DatasetManifestHash, preflight.ManifestHash);
        Assert.Equal((2, 4), (preflight.Counts.Persons, preflight.Counts.Captures));

        // The generated dataset has two attacks that are whole check-ins, and no person with the document of another, no altered and no foreign document.
        Assert.Equal([QualificationCriticalKinds.ImpostorPair, QualificationCriticalKinds.Altered, QualificationCriticalKinds.Foreign], preflight.Counts.CriticalKindsMissing());
        Assert.All(preflight.Validation.Warnings.Where(warning => warning.Split == QualificationSplits.Development), warning => Assert.Equal(QualificationManifestWarningCodes.CriticalKindMissing, warning.Code));

        // A split with nobody in it, a split that does not exist and a manifest that is not there are refused before anything is read.
        Assert.Equal(QualificationRunCodes.SplitEmpty, Assert.Throws<QualificationRunException>(() => QualificationRunner.Preflight(run.ManifestPath, QualificationSplits.Test)).Code);
        Assert.Equal(QualificationRunCodes.SplitUnknown, Assert.Throws<QualificationRunException>(() => QualificationRunner.Preflight(run.ManifestPath, "validation")).Code);
        Assert.Equal(QualificationRunCodes.ManifestUnreadable, Assert.Throws<QualificationRunException>(() => QualificationRunner.Preflight(Path.Combine(run.Folder, "no-such-manifest.json"), QualificationSplits.Development)).Code);
    }

    /// <summary>
    /// Proof (data, the repository is public): the pipeline of the application names the folders of the committed policy files, and a run refuses those folders and the checkout before it reads or
    /// creates anything (the manifest it is given is not even there).
    /// </summary>
    [Fact]
    public async Task QualificationSmoke_TheOutputFolder_IsNeverThePolicyFolderNorTheCheckout_AndARunRefusesItBeforeReadingAnything()
    {
        var published = Path.Combine(AppContext.BaseDirectory, "IdentityPolicies");
        var sources = Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "Casazen.Web", "IdentityPolicies");

        var folders = run.Pipeline.ProtectedPolicyFolders().Select(folder => Path.TrimEndingDirectorySeparator(folder)).ToList();

        Assert.Contains(Path.TrimEndingDirectorySeparator(published), folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.TrimEndingDirectorySeparator(sources), folders, StringComparer.OrdinalIgnoreCase);
        var inPolicies = Path.Combine(published, "qualification-out-test");
        var inCheckout = Path.Combine(IdentityPolicyTestFiles.RepositoryRoot(), "qualification-out-test");
        Assert.Throws<QualificationRunException>(() => run.Pipeline.EnsureOutputAllowed(sources));
        Assert.Throws<QualificationRunException>(() => run.Pipeline.EnsureOutputAllowed(inCheckout));
        run.Pipeline.EnsureOutputAllowed(run.OutDirectory);

        var runner = new QualificationRunner(run.Pipeline, QualificationTargets.Load());
        foreach (var folder in new[] { inPolicies, inCheckout })
        {
            var refusal = await Assert.ThrowsAsync<QualificationRunException>(
                () => runner.RunAsync(Path.Combine(run.Folder, "no-such-manifest.json"), QualificationSplits.Development, folder, CancellationToken.None));

            Assert.Equal(QualificationRunCodes.OutputFolderInvalid, refusal.Code);
            Assert.False(Directory.Exists(folder));
        }
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
