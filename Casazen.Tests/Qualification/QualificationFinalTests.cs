using Xunit;
using Xunit.Abstractions;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T02 (NF-198, D12): the final test of the qualification and the act that freezes what it is run on. Both are opt-in, on a dataset kept outside the repository, and skip with their
// reason otherwise (CI never runs them):
//   1. FREEZE: with CASAZEN_QUALIFICATION_FREEZE=1 (and the manifest and the output folder), the configuration in force (the policy file that a person committed and pinned after the calibration,
//      the weights, the aggregation, the thresholds, the dataset) is written to lock.json. A provisional policy is never frozen, and a lock that is there is never replaced.
//   2. FINAL: with CASAZEN_QUALIFICATION_SPLIT=test (and no freeze), the test REFUSES TO START unless what is in force is exactly the lock (qualification_lock_mismatch, naming the members
//      that differ) and the policy is calibrated; then it runs the real pipeline on the test split ONCE and writes the report of the candidate version, ending with its SHA-256.

/// <summary>The freeze and the final test of the qualification (see the comments at the top of the file).</summary>
public sealed class QualificationFinalTests(ITestOutputHelper output)
{
    /// <summary>Writes the configuration in force to the lock (<c>lock.json</c> of the output folder, or the path of <c>CASAZEN_QUALIFICATION_LOCK</c>).</summary>
    [QualificationFreezeFact]
    public async Task QualificationLock_Freeze_WritesTheConfigurationInForceToTheLock()
    {
        await using var pipeline = await QualificationPipeline.CreateAsync();
        var (manifestHash, testPersons) = QualificationLock.DatasetIdentityOf(QualificationEnvironment.ManifestPath);
        var frozen = QualificationLock.Capture(pipeline, manifestHash, testPersons, pipeline.Clock.GetUtcNow());

        frozen.Save(QualificationEnvironment.LockPath);

        output.WriteLine($"lock written to {QualificationEnvironment.LockPath}: policy {frozen.PolicyVersion}, weights hash {frozen.WeightsHash}, {frozen.TestPersons} persons in the test split.");
    }

    /// <summary>
    /// The final test: refuses to start when the policy, the weights, the aggregation or the manifest differ from the lock, or the policy is provisional; otherwise runs the runner on the test split
    /// once and writes <c>candidate-version-report.md</c> and <c>.json</c> to the output folder.
    /// </summary>
    [QualificationFact(QualificationSplits.Test)]
    public async Task QualificationFinal_UnderTheFrozenLock_RunsTheTestSplitOnceAndWritesTheReport()
    {
        var manifestPath = QualificationEnvironment.ManifestPath;
        var outDirectory = QualificationEnvironment.OutDirectory;
        var targets = QualificationTargets.Load();
        await using var pipeline = await QualificationPipeline.CreateAsync();
        var (manifestHash, testPersons) = QualificationLock.DatasetIdentityOf(manifestPath);
        var current = QualificationLock.Capture(pipeline, manifestHash, testPersons, pipeline.Clock.GetUtcNow());
        var frozen = QualificationLock.Load(QualificationEnvironment.LockPath);
        var runner = new QualificationRunner(pipeline, targets, new QualificationRunOptions { Progress = output.WriteLine });

        // The gate checks the lock before anything of the test split is read, and calls the run once.
        var results = await QualificationFinalGate.RunOnceAsync(frozen, current, outDirectory, () => runner.RunAsync(manifestPath, QualificationSplits.Test, outDirectory, CancellationToken.None));
        var report = CandidateVersionReport.Build(results, frozen, targets);
        report.Write(outDirectory);

        Assert.True(CandidateVersionReport.VerifyHash(report.Markdown));
        output.WriteLine($"report written to {outDirectory}: {report.Verdicts.Count(verdict => verdict.Pass)} of {report.Verdicts.Count} verdicts hold; report-sha256 {report.Sha256}.");
    }
}
