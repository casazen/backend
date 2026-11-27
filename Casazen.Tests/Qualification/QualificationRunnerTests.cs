using Xunit;
using Xunit.Abstractions;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T01 (NF-198, D12): the real runs of the qualification, on the development and calibration splits of a dataset kept outside the repository (it is public, and no photo of a
// person or of a document is ever committed). They are opt-in: they run only when CASAZEN_QUALIFICATION_MANIFEST, CASAZEN_QUALIFICATION_SPLIT (development or calibration) and
// CASAZEN_QUALIFICATION_OUT are set, and skip with their reason otherwise, so CI runs the smoke test and the unit tests only. The split of the final test belongs to the final test
// (QualificationFinalTests), which refuses to start unless the configuration is the frozen one.

/// <summary>The long runs of the qualification pipeline on a real dataset (see the comments at the top of the file); nothing they print is more than ids of the manifest, counts and file names.</summary>
public sealed class QualificationRunnerTests(ITestOutputHelper output)
{
    /// <summary>The real pipeline on the captures of the development or the calibration split: writes <c>results.json</c> to the output folder.</summary>
    [QualificationFact(QualificationSplits.Development, QualificationSplits.Calibration)]
    public async Task QualificationRun_OnTheDevelopmentOrCalibrationSplit_WritesTheResultsOfTheRealPipeline()
    {
        var split = QualificationEnvironment.Split;
        var outDirectory = QualificationEnvironment.OutDirectory;
        var targets = QualificationTargets.Load();
        await using var pipeline = await QualificationPipeline.CreateAsync();
        var runner = new QualificationRunner(pipeline, targets, new QualificationRunOptions { Progress = output.WriteLine });

        var results = await runner.RunAsync(QualificationEnvironment.ManifestPath, split, outDirectory, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(outDirectory, QualificationResults.FileName)));
        Assert.Equal(split, results.Split);
        Assert.NotEmpty(results.Cases);
        var failed = results.Cases.Count(candidate => candidate.Error is not null);
        output.WriteLine($"{results.Cases.Count} cases ({failed} that broke), {results.ImpostorTrials.Count} impostor trials of {results.ImpostorSampling.PairsAvailable} possible pairs; policy {results.Policy.Version}.");
    }
}
