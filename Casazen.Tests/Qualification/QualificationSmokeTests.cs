using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Xunit;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T01 (NF-198, D12): the smoke test of the qualification toolkit, which RUNS IN CI. It writes a tiny dataset of GENERATED pictures to a temporary folder (two persons, a genuine
// capture and an attack each: a card drawn in code with its two sides, and a sequence of noise frames; no photograph of anybody, and the repository is public) and runs the real pipeline over
// it, end to end: the real models, the real registered services, the real readers of the machine. Generated pictures are not faces for the models, so nothing is asserted about a score: the test
// proves that the run completes, that every capture is a case, and that nothing of the ground truth reaches an output.

/// <summary>The collection of the tests that share the one run of the real pipeline over the generated dataset (the models are loaded once).</summary>
[CollectionDefinition(Name)]
public sealed class QualificationSmokeCollection : ICollectionFixture<QualificationSmokeFixture>
{
    public const string Name = "qualification smoke: the real pipeline on generated data";
}

/// <summary>The dataset the smoke test generates and the one run of the real pipeline over it, shared by the tests of the collection.</summary>
public sealed class QualificationSmokeFixture : IAsyncLifetime
{
    /// <summary>
    /// The true values of the documents of the generated dataset, written to their ground-truth files: sentinels no output may contain (the ones of a person are letters and digits that no code, id
    /// or score of the toolkit can spell).
    /// </summary>
    public static IReadOnlyList<string> GroundTruthSentinels { get; } = ["ZQSURNAMEONE", "ZQGIVENONE", "ZQDOCUMENTONE", "ZQSURNAMETWO", "ZQGIVENTWO", "ZQDOCUMENTTWO", "1962-07-19", "1975-11-02", "ZQX"];

    private static readonly string[] Persons = ["p1", "p2"];

    /// <summary>The temporary folder the dataset and the outputs are written to.</summary>
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "casazen-qualification-smoke-" + Guid.NewGuid().ToString("N"));

    /// <summary>The folder the run wrote its output to.</summary>
    public string OutDirectory => Path.Combine(Folder, "out");

    /// <summary>The manifest of the generated dataset.</summary>
    public string ManifestPath => Path.Combine(Folder, "dataset", "manifest.json");

    /// <summary>The pipeline of the run: the real services.</summary>
    public QualificationPipeline Pipeline { get; private set; } = null!;

    /// <summary>What the run gave.</summary>
    public QualificationResults Results { get; private set; } = null!;

    /// <summary>The values of the ground truth that <paramref name="text"/> holds, in any letter case (none for a text that holds nothing of it).</summary>
    public static List<string> LeaksIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. GroundTruthSentinels.Where(sentinel => text.Contains(sentinel, StringComparison.OrdinalIgnoreCase))];
    }

    public async Task InitializeAsync()
    {
        // The published configuration only: nothing of the environment of the machine may change what a test of CI runs.
        Pipeline = await QualificationPipeline.CreateAsync(new QualificationPipelineOptions { IncludeEnvironmentVariables = false });
        WriteDataset(Path.Combine(Folder, "dataset"), Pipeline.Formats.Get("it-cie-3.0") ?? throw new InvalidOperationException("The Italian card is not in the registry."));
        var runner = new QualificationRunner(Pipeline, QualificationTargets.Load());
        Results = await runner.RunAsync(ManifestPath, QualificationSplits.Development, OutDirectory, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (Pipeline is not null)
            await Pipeline.DisposeAsync();

        if (Directory.Exists(Folder))
            Directory.Delete(Folder, recursive: true);
    }

    private static void WriteDataset(string folder, DocumentFormat format)
    {
        Directory.CreateDirectory(folder);
        var truths = new Dictionary<string, (string Surname, string Given, string Number, string Birth)>
        {
            ["p1"] = ("ZQSURNAMEONE", "ZQGIVENONE", "ZQDOCUMENTONE", "1962-07-19"),
            ["p2"] = ("ZQSURNAMETWO", "ZQGIVENTWO", "ZQDOCUMENTTWO", "1975-11-02"),
        };
        var captures = new List<QualificationCapture>();
        var seed = 0;
        foreach (var person in Persons)
        {
            var directory = Path.Combine(folder, person);
            Directory.CreateDirectory(directory);

            // The card, drawn in code, and its true values.
            using (var front = DocumentImageFactory.Render(format, DocumentSide.Front, DocumentImageFactory.WholeCard))
                File.WriteAllBytes(Path.Combine(directory, "front.png"), CardPngEncoder.Encode(front));

            using (var back = DocumentImageFactory.Render(format, DocumentSide.Back, DocumentImageFactory.WholeCard))
                File.WriteAllBytes(Path.Combine(directory, "back.png"), CardPngEncoder.Encode(back));

            var truth = truths[person];
            File.WriteAllText(
                Path.Combine(directory, "truth.json"),
                $$"""{ "surname": "{{truth.Surname}}", "givenNames": "{{truth.Given}}", "birthDate": "{{truth.Birth}}", "documentNumber": "{{truth.Number}}", "nationality": "ZQX" }""");

            // Two sequences of noise frames at 8 frames a second, three seconds long: the genuine one and the attack.
            foreach (var (name, kind, attackClass) in new[] { ("genuine", QualificationCaptureKinds.Genuine, (string?)null), ("attack", QualificationCaptureKinds.Attack, QualificationAttackClasses.PhotoPrint) })
            {
                var sequence = Path.Combine(directory, "seq-" + name);
                Directory.CreateDirectory(sequence);
                var timestamps = new List<long>();
                for (var index = 0; index <= 24; index++)
                {
                    File.WriteAllBytes(Path.Combine(sequence, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{index:000}.jpg")), TestJpeg.Noise(96, 72, ++seed));
                    timestamps.Add(index * 125L);
                }

                File.WriteAllText(Path.Combine(sequence, "timestamps.json"), "[" + string.Join(",", timestamps) + "]");
                captures.Add(new QualificationCapture(
                    (kind == QualificationCaptureKinds.Genuine ? "g-" : "a-") + person,
                    person,
                    "session-" + person,
                    "device-1",
                    IdentityChannels.DesktopChromium,
                    kind,
                    attackClass,
                    new QualificationDocument(format.Id, $"{person}/front.png", $"{person}/back.png", person, false, false, $"{person}/truth.json"),
                    new QualificationSequence($"{person}/seq-{name}", $"{person}/seq-{name}/timestamps.json", [PresenceActions.TurnLeft, PresenceActions.LookUp, PresenceActions.TurnRight])));
            }
        }

        var manifest = new QualificationDatasetManifest(
            "smoke",
            "1",
            new QualificationRights("The smoke test of the qualification toolkit", "generated-in-code", "the tests", false),
            false,
            [.. Persons.Select(person => new QualificationPerson(person, QualificationSplits.Development))],
            captures);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest.ToJson());
    }
}

/// <summary>The smoke test of the qualification toolkit (see the comments at the top of the file).</summary>
[Collection(QualificationSmokeCollection.Name)]
public sealed class QualificationSmokeTests(QualificationSmokeFixture run)
{
    [Fact]
    public void QualificationSmoke_RealPipelineOnGeneratedData_CompletesAndHasACaseForEveryCapture()
    {
        var results = run.Results;

        Assert.Equal(QualificationSplits.Development, results.Split);
        Assert.Equal(["a-p1", "a-p2", "g-p1", "g-p2"], results.Cases.Select(candidate => candidate.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, results.Cases.Count(candidate => candidate.Kind == QualificationCaseKinds.Genuine));
        Assert.Equal(2, results.Cases.Count(candidate => candidate.Kind == QualificationCaseKinds.Attack));

        // Nothing of a case broke: the pipeline of the application ran on every capture, document and sequence.
        Assert.All(results.Cases, candidate =>
        {
            Assert.Null(candidate.Error);
            Assert.NotNull(candidate.Document);
            Assert.NotNull(candidate.Presence);
            Assert.NotNull(candidate.Decision);
        });
        Assert.Equal(25, results.Cases[0].Presence!.Frames);

        // The configuration in force is the published one, read by the application's own provider.
        Assert.Equal(run.Pipeline.PolicySnapshot.Hash, results.Policy.CanonicalHash);
        Assert.Contains(results.Models, model => model.Role == BiometricModelRoles.FaceRecognition && model.Loaded && model.WeightSha256 is { Length: 64 });
        Assert.True(File.Exists(Path.Combine(run.OutDirectory, QualificationResults.FileName)));
        Assert.Equal(results.Cases.Count, QualificationResults.Load(Path.Combine(run.OutDirectory, QualificationResults.FileName)).Cases.Count);
    }

    /// <summary>Proof (privacy, ADR-013-R2, R17): nothing of the ground truth, and no picture, reaches a file or a text the run produces.</summary>
    [Fact]
    public void QualificationSmoke_OutputContainsNoGroundTruth()
    {
        var files = Directory.EnumerateFiles(run.OutDirectory, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.EndsWith(".json", file, StringComparison.Ordinal));

        var texts = files.Select(File.ReadAllText).Append(run.Results.ToJson()).ToList();
        foreach (var text in texts)
            Assert.Empty(QualificationSmokeFixture.LeaksIn(text));
    }

    /// <summary>The check above is not blind: a text that holds a value of the ground truth (in any letter case) is found.</summary>
    [Fact]
    public void QualificationSmoke_TheLeakCheck_FindsAValueOfTheGroundTruthWhereOneIs()
    {
        Assert.Empty(QualificationSmokeFixture.LeaksIn("only ids, codes and counts: g-p1 review 3/4"));
        Assert.Equal(["ZQSURNAMEONE"], QualificationSmokeFixture.LeaksIn("the surname was zqsurnameone on the card"));
        Assert.Equal(["ZQGIVENTWO", "1975-11-02"], QualificationSmokeFixture.LeaksIn("ZQGIVENTWO born 1975-11-02"));
    }
}
