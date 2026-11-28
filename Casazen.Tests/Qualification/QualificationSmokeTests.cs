using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Xunit;

namespace Casazen.Tests.Qualification;

[CollectionDefinition(Name)]
public sealed class QualificationSmokeCollection : ICollectionFixture<QualificationSmokeFixture>
{
    public const string Name = "qualification smoke: the real pipeline on generated data";
}

public sealed class QualificationSmokeFixture : IAsyncLifetime
{
    public static IReadOnlyList<string> GroundTruthSentinels { get; } = ["ZQSURNAMEONE", "ZQGIVENONE", "ZQDOCUMENTONE", "ZQSURNAMETWO", "ZQGIVENTWO", "ZQDOCUMENTTWO", "1962-07-19", "1975-11-02", "ZQX"];

    private static readonly string[] Persons = ["p1", "p2"];

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "casazen-qualification-smoke-" + Guid.NewGuid().ToString("N"));

    public string OutDirectory => Path.Combine(Folder, "out");

    public string ManifestPath => Path.Combine(Folder, "dataset", "manifest.json");

    public QualificationPipeline Pipeline { get; private set; } = null!;

    public QualificationResults Results { get; private set; } = null!;

    public static List<string> LeaksIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. GroundTruthSentinels.Where(sentinel => text.Contains(sentinel, StringComparison.OrdinalIgnoreCase))];
    }

    public async Task InitializeAsync()
    {
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

            using (var front = DocumentImageFactory.Render(format, DocumentSide.Front, DocumentImageFactory.WholeCard))
                File.WriteAllBytes(Path.Combine(directory, "front.png"), CardPngEncoder.Encode(front));

            using (var back = DocumentImageFactory.Render(format, DocumentSide.Back, DocumentImageFactory.WholeCard))
                File.WriteAllBytes(Path.Combine(directory, "back.png"), CardPngEncoder.Encode(back));

            var truth = truths[person];
            File.WriteAllText(
                Path.Combine(directory, "truth.json"),
                $$"""{ "surname": "{{truth.Surname}}", "givenNames": "{{truth.Given}}", "birthDate": "{{truth.Birth}}", "documentNumber": "{{truth.Number}}", "nationality": "ZQX" }""");

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

        Assert.All(results.Cases, candidate =>
        {
            Assert.Null(candidate.Error);
            Assert.NotNull(candidate.Document);
            Assert.NotNull(candidate.Presence);
            Assert.NotNull(candidate.Decision);
        });
        Assert.Equal(25, results.Cases[0].Presence!.Frames);

        Assert.Equal(run.Pipeline.PolicySnapshot.Hash, results.Policy.CanonicalHash);
        Assert.Contains(results.Models, model => model.Role == BiometricModelRoles.FaceRecognition && model.Loaded && model.WeightSha256 is { Length: 64 });
        Assert.True(File.Exists(Path.Combine(run.OutDirectory, QualificationResults.FileName)));
        Assert.Equal(results.Cases.Count, QualificationResults.Load(Path.Combine(run.OutDirectory, QualificationResults.FileName)).Cases.Count);
    }

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

    [Fact]
    public void QualificationSmoke_TheLeakCheck_FindsAValueOfTheGroundTruthWhereOneIs()
    {
        Assert.Empty(QualificationSmokeFixture.LeaksIn("only ids, codes and counts: g-p1 review 3/4"));
        Assert.Equal(["ZQSURNAMEONE"], QualificationSmokeFixture.LeaksIn("the surname was zqsurnameone on the card"));
        Assert.Equal(["ZQGIVENTWO", "1975-11-02"], QualificationSmokeFixture.LeaksIn("ZQGIVENTWO born 1975-11-02"));
    }
}
