using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T01 (NF-198, D12): the logic of the runner, run on the REAL pipeline of the application with the five readings of a PICTURE replaced by doubles that read the picture as a
// code, so that the whole path of a capture is walked in CI: the document stage (geometry, side, MRZ, coherence), the presence (intake, validation, the frames the server chooses,
// liveness, match), the decision of the engine, the impostor trials derived from the sequences (all, and a deterministic sample), the paired scores of a candidate model and the metrics.
// The doubles are the face detector (it finds the stand-in of the portrait on a card and, in a frame of 96 x 72, the face the first blocks of its pixels spell), the embedder (the person the
// markers of a card or a frame spell), the anti-spoofing model (live or not, spelled the same way), the MRZ reader (it answers for the back of a card) and the format (the Italian card
// asking only for the checks of the MRZ). The validation of the sequence, the choice of the frames, the matcher, the coherence checker, the engine, the registry and the statistics are the
// application's own. Every picture is generated in code; the repository is public.

internal static class PixelCode
{
    public const int FrameWidth = 96;
    public const int FrameHeight = 72;

    public const int YawBlock = 0;
    public const int PitchBlock = 1;
    public const int FaceBlock = 2;
    public const int PersonBlock = 3;
    public const int LiveBlock = 4;

    public const double YawStep = 5;
    public const double PitchStep = 3;

    public const int CardWidth = DocumentImageFactory.Width;
    public const int CardHeight = DocumentImageFactory.Height;

    public const int CardPersonX = 88;
    public const int CardSideX = 96;
    public const int CardMarkY = 78;

    public const byte FrontLevel = 40;
    public const byte BackLevel = 200;

    public static byte PersonLevel(int person) => (byte)(30 + (25 * person));

    public static int PersonOfLevel(int level) => Math.Clamp((int)Math.Round((level - 30) / 25.0), 0, 7);

    public static int Block(RasterImage frame, int block) => frame.Rgb[((4 * frame.Width) + (block * 8) + 4) * 3];

    public static int Level(RasterImage picture, int x, int y) => picture.Rgb[((y * picture.Width) + x) * 3];

    public static bool IsFrame(RasterImage picture) => picture.Width == FrameWidth && picture.Height == FrameHeight;

    public static bool IsCard(RasterImage picture) => picture.Width == CardWidth && picture.Height == CardHeight;

    public static byte PoseLevel(double degrees, double step) => (byte)(Math.Clamp((int)Math.Round(degrees / step) + 8, 0, 15) * 16);

    public static double PoseDegrees(int level, double step) => ((int)Math.Round(level / 16.0) - 8) * step;

    public static byte[] FramePixels(ScriptedFrame frame, int person, bool live)
    {
        var pixels = (byte[])frame.Gray.Clone();
        var angles = FacePose.Estimate(frame.Faces[0]);
        Stamp(pixels, YawBlock, PoseLevel(angles.YawDegrees, YawStep));
        Stamp(pixels, PitchBlock, PoseLevel(angles.PitchDegrees, PitchStep));
        Stamp(pixels, FaceBlock, 255);
        Stamp(pixels, PersonBlock, PersonLevel(person));
        Stamp(pixels, LiveBlock, live ? (byte)255 : (byte)0);
        return pixels;
    }

    private static void Stamp(byte[] pixels, int block, byte level)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
                pixels[(y * FrameWidth) + (block * 8) + x] = level;
        }
    }

    public static void StampCard(RasterImage card, int person, byte sideLevel)
    {
        for (var y = CardMarkY; y < CardMarkY + 16; y++)
        {
            for (var x = CardPersonX; x < CardPersonX + 16; x++)
            {
                var level = x < CardSideX ? PersonLevel(person) : sideLevel;
                var at = ((y * card.Width) + x) * 3;
                card.Rgb[at] = level;
                card.Rgb[at + 1] = level;
                card.Rgb[at + 2] = level;
            }
        }
    }
}

internal sealed class PixelFaceDetector(IFaceDetector cards) : IFaceDetector
{
    public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
    {
        if (!PixelCode.IsFrame(image))
            return [.. cards.Detect(image, minScore, cancellationToken).Select(Frontal)];

        if (PixelCode.Block(image, PixelCode.FaceBlock) < 128)
            return [];

        var yaw = PixelCode.PoseDegrees(PixelCode.Block(image, PixelCode.YawBlock), PixelCode.YawStep);
        var pitch = PixelCode.PoseDegrees(PixelCode.Block(image, PixelCode.PitchBlock), PixelCode.PitchStep);
        return [SequenceTestFrames.Face(image.Width, image.Height, yaw, pitch)];
    }

    private static DetectedFace Frontal(DetectedFace box)
    {
        var centreX = box.X + (box.Width / 2);
        var eyesY = box.Y + (0.38f * box.Height);
        var eyesToMouth = 1.1546f * 0.55f * box.Width;
        float[] landmarks =
        [
            centreX - (0.275f * box.Width), eyesY,
            centreX + (0.275f * box.Width), eyesY,
            centreX, eyesY + (0.495f * eyesToMouth),
            centreX - (0.3f * box.Width), eyesY + eyesToMouth,
            centreX + (0.3f * box.Width), eyesY + eyesToMouth,
        ];
        return new DetectedFace(box.X, box.Y, box.Width, box.Height, box.Score, landmarks, box.AreaRatio);
    }
}

internal sealed class PixelFaceEmbedder : IFaceEmbedder
{
    public float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken)
    {
        var level = PixelCode.IsFrame(image) ? PixelCode.Block(image, PixelCode.PersonBlock) : PixelCode.Level(image, PixelCode.CardPersonX + 4, PixelCode.CardMarkY + 4);
        var vector = new float[8];
        vector[PixelCode.PersonOfLevel(level)] = 1;
        return vector;
    }
}

internal sealed class PixelAntiSpoofing : IAntiSpoofingService
{
    public async Task<bool> DetectLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        return (await ScoreLivenessAsync(imageStream, cancellationToken)).IsLive;
    }

    public async Task<LivenessResult> ScoreLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        using var copy = new MemoryStream();
        await imageStream.CopyToAsync(copy, cancellationToken);
        using var frame = RasterImage.Decode(copy.ToArray(), 1_000_000);
        var score = PixelCode.Block(frame, PixelCode.LiveBlock) > 128 ? 0.95 : 0.30;
        return new LivenessResult(score >= 0.7, score);
    }
}

// The MRZ spells the true values of the owner of the card, as the dataset declares them for the booking.
internal sealed class MarkerMrzReader : IMrzExtractionService
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int Width, int Height)> _reads = new();

    public IReadOnlyList<(int Width, int Height)> Reads => [.. _reads];

    public async Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        return (await ReadAsync(imageStream, cancellationToken)).Result;
    }

    public async Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        using var copy = new MemoryStream();
        await imageStream.CopyToAsync(copy, cancellationToken);
        using var picture = RasterImage.Decode(copy.ToArray(), 1_000_000);
        _reads.Enqueue((picture.Width, picture.Height));
        if (StampLevelAt(picture, PixelCode.CardSideX + 4, PixelCode.CardMarkY + 4) < 128)
            return new MrzReading(null, []);

        var person = PixelCode.PersonOfLevel(StampLevelAt(picture, PixelCode.CardPersonX + 4, PixelCode.CardMarkY + 4));
        return new MrzReading(
            new MrzExtractionResult
            {
                DocumentType = "IdCard",
                Surname = QualificationLogicFixture.TrueSurname(person),
                GivenNames = QualificationLogicFixture.TrueGivenNames(person),
                Nationality = "ITA",
                DocumentNumber = QualificationLogicFixture.TrueNumber(person),
                DateOfBirth = new DateTime(1980, 1, 1),
                ExpirationDate = new DateTime(2040, 1, 1),
                IsExpired = false,
                Sex = "F",
                IssuingState = "ITA",
                DocumentCode = "I<",
                MrzType = "TD1",
                CheckDigitsValid = true,
                Confidence = 0.95,
            },
            []);
    }

    // The runner hands the reader the card cut out of the picture, so the stamp is read where the whole picture has it or where the card has it.
    private static int StampLevelAt(RasterImage picture, int x, int y)
    {
        if (PixelCode.IsCard(picture))
            return PixelCode.Level(picture, x, y);

        var place = DocumentImageFactory.WholeCard;
        if (Math.Abs(picture.Width - place.Width) <= 4 && Math.Abs(picture.Height - place.Height) <= 4)
            return PixelCode.Level(picture, x - place.Left, y - place.Top);

        return -1;
    }
}

[CollectionDefinition(Name)]
public sealed class QualificationLogicCollection : ICollectionFixture<QualificationLogicFixture>
{
    public const string Name = "qualification logic: the real pipeline with the pictures read as a code";
}

public sealed class QualificationLogicFixture : IAsyncLifetime
{
    public const int Persons = 4;

    public const int Cap = 5;

    public static string TrueSurname(int person) => $"ZQSURNAME{person}";

    public static string TrueGivenNames(int person) => $"ZQGIVEN{person}";

    public static string TrueNumber(int person) => $"ZQNUMBER{person}";

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "casazen-qualification-logic-" + Guid.NewGuid().ToString("N"));

    public string ManifestPath => Path.Combine(Folder, "dataset", "manifest.json");

    public QualificationPipeline Pipeline { get; private set; } = null!;

    public QualificationResults Results { get; private set; } = null!;

    public QualificationResults Capped { get; private set; } = null!;

    public string OutDirectory => Path.Combine(Folder, "out");

    public static IReadOnlyDictionary<int, string> PoolCaptures { get; } = Enumerable.Range(1, Persons).ToDictionary(person => person, person => $"g-p{person}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Folder);

        var formats = Path.Combine(Folder, "formats");
        Directory.CreateDirectory(formats);
        var card = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "IdentityFormats", "it-cie-3.0.json")))!.AsObject();
        card["requiredChecks"] = new JsonArray("layout", "portrait", "mrz_check_digits", "expiry");
        File.WriteAllText(Path.Combine(formats, "it-cie-3.0.json"), card.ToJsonString());

        Pipeline = await QualificationPipeline.CreateAsync(new QualificationPipelineOptions
        {
            IncludeEnvironmentVariables = false,
            ConfigureServices = services =>
            {
                services.AddSingleton<IFaceDetector>(new PixelFaceDetector(new PortraitStandInDetector()));
                services.AddSingleton<IFaceEmbedder, PixelFaceEmbedder>();
                services.AddScoped<IAntiSpoofingService, PixelAntiSpoofing>();
                services.AddScoped<IMrzExtractionService, MarkerMrzReader>();
                services.PostConfigure<IdentityVerificationOptions>(options => options.FormatsDirectory = formats);
                services.PostConfigure<BiometricModelOptions>(options =>
                {
                    options.FaceRecognitionCandidate = CandidateOf<FaceRecognitionCandidateOptions>(options.FaceRecognition, "recognition-copy.onnx");
                    options.AntiSpoofingCandidate = CandidateOf<AntiSpoofingCandidateOptions>(options.AntiSpoofing, "anti-spoofing-copy.onnx");
                });
            },
        });

        WriteDataset(Path.Combine(Folder, "dataset"));
        var targets = QualificationTargets.Load();
        Results = await new QualificationRunner(Pipeline, targets).RunAsync(ManifestPath, QualificationSplits.Development, OutDirectory, CancellationToken.None);
        Capped = await new QualificationRunner(Pipeline, targets, new QualificationRunOptions { MaxImpostorTrials = Cap })
            .RunAsync(ManifestPath, QualificationSplits.Development, Path.Combine(Folder, "out-capped"), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (Pipeline is not null)
            await Pipeline.DisposeAsync();

        if (Directory.Exists(Folder))
            Directory.Delete(Folder, recursive: true);
    }

    private T CandidateOf<T>(ModelFileOptions bundled, string fileName)
        where T : ModelFileOptions, new()
    {
        var target = Path.Combine(Folder, fileName);
        if (!File.Exists(target))
            File.Copy(Path.Combine(AppContext.BaseDirectory, bundled.Path), target);

        return new T { Name = bundled.Name + " (copy)", Version = "test", License = bundled.License, Provenance = "A copy of the bundled model, for the tests of the qualification runner.", Path = target, Sha256 = bundled.Sha256 };
    }

    private static void WriteDataset(string folder)
    {
        var format = IdentityTestDocuments.Cie();
        var policy = IdentityTestPolicy.Shipped();
        Directory.CreateDirectory(folder);
        for (var person = 1; person <= Persons; person++)
        {
            var directory = Path.Combine(folder, $"p{person}");
            Directory.CreateDirectory(directory);
            using (var front = DocumentImageFactory.Render(format, DocumentSide.Front, DocumentImageFactory.WholeCard))
            {
                PixelCode.StampCard(front, person, PixelCode.FrontLevel);
                File.WriteAllBytes(Path.Combine(directory, "front.png"), CardPngEncoder.Encode(front));
                File.WriteAllBytes(Path.Combine(directory, "front-altered.png"), CardPngEncoder.Encode(front));
            }

            using (var back = DocumentImageFactory.Render(format, DocumentSide.Back, DocumentImageFactory.WholeCard))
            {
                PixelCode.StampCard(back, person, PixelCode.BackLevel);
                File.WriteAllBytes(Path.Combine(directory, "back.png"), CardPngEncoder.Encode(back));
            }

            File.WriteAllText(Path.Combine(directory, "truth.json"), $$"""{ "surname": "{{TrueSurname(person)}}", "givenNames": "{{TrueGivenNames(person)}}", "documentNumber": "{{TrueNumber(person)}}" }""");
            WriteSequence(Path.Combine(directory, "seq"), policy, person, live: true);
        }

        WriteSequence(Path.Combine(folder, "p1", "seq-attack"), policy, 1, live: false);

        QualificationDocument Document(int owner, string front = "front.png", bool altered = false) =>
            new(IdentityTestDocuments.Cie().Id, $"p{owner}/{front}", $"p{owner}/back.png", $"p{owner}", altered, false, $"p{owner}/truth.json");

        QualificationSequence Sequence(int person, string name = "seq") =>
            new($"p{person}/{name}", $"p{person}/{name}/timestamps.json", [.. SequenceTestFrames.DefaultSteps]);

        QualificationCapture Capture(string id, int person, string kind, QualificationDocument document, QualificationSequence sequence, string? attackClass = null) =>
            new(id, $"p{person}", $"session-p{person}", "device-1", IdentityChannels.DesktopChromium, kind, attackClass, document, sequence);

        var captures = new List<QualificationCapture>();
        for (var person = 1; person <= Persons; person++)
            captures.Add(Capture($"g-p{person}", person, QualificationCaptureKinds.Genuine, Document(person), Sequence(person)));

        captures.Add(Capture("a-p1", 1, QualificationCaptureKinds.Attack, Document(1), Sequence(1, "seq-attack"), QualificationAttackClasses.PhotoPrint));
        captures.Add(Capture("i-p2", 2, QualificationCaptureKinds.Genuine, Document(3), Sequence(2)));
        captures.Add(Capture("h-p4", 4, QualificationCaptureKinds.Genuine, Document(4, "front-altered.png", altered: true), Sequence(4)));
        var manifest = new QualificationDatasetManifest(
            "logic",
            "1",
            new QualificationRights("The tests of the qualification runner", "generated-in-code", "the tests", false),
            false,
            [.. Enumerable.Range(1, Persons).Select(person => new QualificationPerson($"p{person}", QualificationSplits.Development))],
            captures);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest.ToJson());
    }

    private static void WriteSequence(string directory, IdentityDecisionPolicy policy, int person, bool live)
    {
        Directory.CreateDirectory(directory);
        var scripted = SequenceTestFrames.Genuine(policy);
        for (var index = 0; index < scripted.Frames.Count; index++)
            File.WriteAllBytes(Path.Combine(directory, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{index:000}.jpg")), TestJpeg.Gray(PixelCode.FrameWidth, PixelCode.FrameHeight, PixelCode.FramePixels(scripted.Frames[index], person, live)));

        File.WriteAllText(Path.Combine(directory, "timestamps.json"), "[" + string.Join(",", scripted.Frames.Select(frame => frame.TimestampMs)) + "]");
    }
}

[Collection(QualificationLogicCollection.Name)]
public sealed class QualificationRunnerLogicTests(QualificationLogicFixture run)
{
    private QualificationCaseResult Case(string id) => run.Results.Cases.Single(candidate => candidate.Id == id);

    [Fact]
    public void Run_EveryCaptureOfTheSplitIsACase_OfItsKind_AndNoneBroke()
    {
        Assert.Equal(["a-p1", "g-p1", "g-p2", "g-p3", "g-p4", "h-p4", "i-p2"], run.Results.Cases.Select(candidate => candidate.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            (5, 1, 1),
            (run.Results.Cases.Count(candidate => candidate.Kind == QualificationCaseKinds.Genuine),
             run.Results.Cases.Count(candidate => candidate.Kind == QualificationCaseKinds.Attack),
             run.Results.Cases.Count(candidate => candidate.Kind == QualificationCaseKinds.ImpostorPair)));
        Assert.Equal("i-p2", run.Results.Cases.Single(candidate => candidate.Kind == QualificationCaseKinds.ImpostorPair).Id);
        Assert.All(run.Results.Cases, candidate => Assert.Null(candidate.Error));
        Assert.True(File.Exists(Path.Combine(run.OutDirectory, QualificationResults.FileName)));
        Assert.Equal(run.Results.ToJson(), QualificationResults.Load(Path.Combine(run.OutDirectory, QualificationResults.FileName)).ToJson());
    }

    [Fact]
    public void Run_AGenuineCheckIn_HasACompleteDocument_AValidLivePresence_ASimilarityOfOne_AndIsApproved()
    {
        foreach (var id in new[] { "g-p1", "g-p2", "g-p3", "g-p4" })
        {
            var genuine = Case(id);

            Assert.True(genuine.Document!.Complete, id);
            Assert.Equal(QualificationDecisionCodes.CoherencePassed, genuine.Document.Coherence);
            Assert.True(genuine.Document.MrzFormatAgrees);
            Assert.Empty(genuine.Document.Codes);
            Assert.Equal(["front", "back"], genuine.Document.Sides.Select(side => side.Side));
            Assert.Equal(["front", "back"], genuine.Document.Sides.Select(side => side.Shown));
            Assert.Equal([false, true], genuine.Document.Sides.Select(side => side.MrzRead));
            var presence = genuine.Presence!;
            Assert.True(presence.Accepted, id);
            Assert.True(presence.ValidationPassed);
            Assert.Empty(presence.ValidationFailures);
            Assert.Equal(QualificationDecisionCodes.LivenessPassed, presence.LivenessState);
            Assert.Equal(run.Pipeline.ActivePolicy.Match.Frames, presence.MatchFrames.Count);
            Assert.True(presence.LivenessFrames.Count >= run.Pipeline.ActivePolicy.Presence.Liveness.Frames);
            Assert.All(presence.MatchFrames, frame => Assert.Contains(frame, presence.LivenessFrames));
            Assert.Equal(presence.LivenessFrames.Count, presence.LivenessScores.Count);
            Assert.All(presence.LivenessScores, score => Assert.Equal(0.95, score));
            Assert.Equal(QualificationDecisionCodes.MatchOk, presence.MatchState);
            Assert.True(presence.AcceptPassed);
            Assert.InRange(presence.Similarity!.Value, 0.999, 1.0);
            Assert.Equal(QualificationDecisionCodes.FinalAutoApproved, genuine.Decision!.Final);
            Assert.Equal(QualificationDecisionCodes.ShadowApprove, genuine.Decision.Shadow);
            Assert.Equal("automatic", genuine.Decision.Mode);
        }
    }

    [Fact]
    public void Run_TheMrzIsReadOnTheCardOfEveryPicture_NeverOnTheWholePicture()
    {
        var reads = Assert.IsType<MarkerMrzReader>(run.Pipeline.Mrz).Reads;

        Assert.NotEmpty(reads);
        Assert.All(reads, read =>
        {
            Assert.InRange(read.Width, 470, 495);
            Assert.InRange(read.Height, 295, 315);
        });
    }

    [Fact]
    public void Run_TheStagesAreTimed_AndTheFieldsOfTheGroundTruthAreCountedAsReadOrNot_NeverQuoted()
    {
        var genuine = Case("g-p1");

        foreach (var stage in new[] { QualificationStages.DocumentCapture, QualificationStages.DocumentMrz, QualificationStages.DocumentGeometry, QualificationStages.DocumentCoherence, QualificationStages.PresenceIntake, QualificationStages.PresenceValidation, QualificationStages.PresenceSelection, QualificationStages.PresenceLiveness, QualificationStages.PresenceMatch, QualificationStages.Decision })
            Assert.True(genuine.StageMs[stage] >= 0, stage);

        Assert.Contains(genuine.Document!.Fields, field => field.Field == "surname" && field.Total >= 1 && field.Exact == 0);
        Assert.Contains(genuine.Document.Fields, field => field.Field == "documentNumber.mrz" && field.Total >= 1 && field.Exact == field.Total);
        Assert.Equal(run.Results.Metrics.Latency.Select(stage => stage.Stage), QualificationStages.All.Where(stage => run.Results.Metrics.Latency.Any(entry => entry.Stage == stage)));
        Assert.All(run.Results.Metrics.Latency, stage => Assert.True(stage.P95Ms >= stage.P50Ms && stage.MaxMs >= stage.P95Ms));
        Assert.DoesNotContain("ZQSURNAME", run.Results.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ZQNUMBER", run.Results.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ZQGIVEN", run.Results.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_AnAttack_IsNotAcceptedAsAPersonPresent_ByItsLiveness_AndIsReviewedNeverApproved()
    {
        var attack = Case("a-p1");

        Assert.Equal(QualificationAttackClasses.PhotoPrint, attack.AttackClass);
        Assert.True(attack.Presence!.ValidationPassed);
        Assert.False(attack.Presence.Accepted);
        Assert.Equal(QualificationDecisionCodes.LivenessFailed, attack.Presence.LivenessState);
        Assert.All(attack.Presence.LivenessScores, score => Assert.Equal(0.30, score));

        Assert.Equal(QualificationDecisionCodes.MatchOk, attack.Presence.MatchState);
        Assert.Equal(QualificationDecisionCodes.FinalReview, attack.Decision!.Final);
        Assert.Equal(QualificationDecisionCodes.ShadowReview, attack.Decision.Shadow);
        var photo = run.Results.Metrics.Apcer.Single(entry => entry.Class == QualificationAttackClasses.PhotoPrint).Rate;
        Assert.Equal((0L, 1L), (photo.Count, photo.Total));
        Assert.Equal(0.0, run.Results.Metrics.ApcerMax);
    }

    [Fact]
    public void Run_APersonPresentWithTheCardOfAnother_IsAnImpostorTrial_WithNothingInCommon_AndIsReviewed()
    {
        var impostor = Case("i-p2");

        Assert.True(impostor.Presence!.Accepted);
        Assert.Equal(0.0, impostor.Presence.Similarity);
        Assert.Equal(false, impostor.Presence.AcceptPassed);
        Assert.Equal(QualificationDecisionCodes.FinalReview, impostor.Decision!.Final);
        Assert.Equal(QualificationDecisionCodes.ShadowReview, impostor.Decision.Shadow);
    }

    [Fact]
    public void Run_AnAlteredDocumentThePipelineCannotTell_IsApproved_AndCountedAsACriticalFalseApproval()
    {
        var altered = Case("h-p4");
        var critical = run.Results.Metrics.CriticalFalseApprovals;

        Assert.True(altered.Altered);
        Assert.Equal(QualificationDecisionCodes.FinalAutoApproved, altered.Decision!.Final);
        Assert.Equal(1, critical.Total);
        Assert.Equal(1, critical.ByKind["altered"]);
        Assert.Equal(0, critical.ByKind[QualificationCaseKinds.Attack]);
        Assert.Equal((1L, 1L), (run.Results.Metrics.Document.HostileLetThrough.Count, run.Results.Metrics.Document.HostileLetThrough.Total));
    }

    [Fact]
    public void Run_TheImpostorTrials_AreTheSequenceOfEveryPersonAgainstTheCardsOfTheOthers_AllOfThem()
    {
        var sampling = run.Results.ImpostorSampling;
        var expected = new[] { ("g-p1", 1), ("g-p2", 2), ("g-p3", 3), ("g-p4", 4), ("h-p4", 4) }
            .SelectMany(source => Enumerable.Range(1, QualificationLogicFixture.Persons).Where(person => person != source.Item2).Select(person => $"{source.Item1}>{QualificationLogicFixture.PoolCaptures[person]}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal((15L, 15L, false, 0), (sampling.PairsAvailable, sampling.PairsRun, sampling.Sampled, sampling.SequencesWithoutFrames));
        Assert.Equal(expected, run.Results.ImpostorTrials.Select(trial => $"{trial.SequenceCase}>{trial.DocumentCase}").Order(StringComparer.Ordinal));
        Assert.All(run.Results.ImpostorTrials, trial => Assert.Equal((0.0, 0.0), (trial.Similarity, trial.MinSimilarity)));
        Assert.All(run.Results.ImpostorTrials, trial => Assert.True(trial.MatchMs >= 0));

        Assert.All(run.Results.ImpostorTrials, trial => Assert.NotNull(trial.Couple));
        Assert.Equal(12, run.Results.ImpostorTrials.Select(trial => trial.Couple).Distinct().Count());
        Assert.Equal(QualificationMetrics.CoupleKey("p2", "p3"), run.Results.ImpostorTrials.Single(trial => trial.SequenceCase == "g-p2" && trial.DocumentCase == "g-p3").Couple);
        Assert.Equal(QualificationMetrics.CoupleKey("p2", "p3"), Case("i-p2").Couple);
        Assert.All(run.Results.Cases.Where(candidate => candidate.Id != "i-p2"), candidate => Assert.Null(candidate.Couple));
    }

    [Fact]
    public void Run_WithMoreTrialsThanTheLimit_TakesTheDeterministicSample_AndSaysHowManyThereWere()
    {
        var sampling = run.Capped.ImpostorSampling;
        var share = (double)QualificationLogicFixture.Cap / 15;
        var expected = run.Results.ImpostorTrials
            .Where(trial => QualificationRunner.IsSampled(trial.SequenceCase, trial.DocumentCase, share))
            .Select(trial => $"{trial.SequenceCase}>{trial.DocumentCase}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal((15L, true, QualificationLogicFixture.Cap), (sampling.PairsAvailable, sampling.Sampled, sampling.Cap));
        Assert.Equal(expected.Count, sampling.PairsRun);
        Assert.InRange(sampling.PairsRun, 1, 14);
        Assert.Equal(expected, run.Capped.ImpostorTrials.Select(trial => $"{trial.SequenceCase}>{trial.DocumentCase}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Run_TheMetricsAreThoseOfTheCases_AtTheThresholdsOfThePolicy()
    {
        var metrics = run.Results.Metrics;

        Assert.Equal((0L, 16L), (metrics.Fmr.Count, metrics.Fmr.Total));
        Assert.Equal((0L, 12L), (metrics.FmrCouples.Count, metrics.FmrCouples.Total));
        Assert.Equal((0L, 4L), (metrics.Fnmr.Count, metrics.Fnmr.Total));

        Assert.All(metrics.AttacksNotJudged, entry => Assert.Equal(0, entry.NotJudged));
        Assert.Equal((1, 1, 1, 0), (metrics.CriticalFalseApprovals.Decided["attack"], metrics.CriticalFalseApprovals.Decided["impostor_pair"], metrics.CriticalFalseApprovals.Decided["altered"], metrics.CriticalFalseApprovals.Decided["foreign"]));

        Assert.Equal((0L, 6L), (metrics.Bpcer.Count, metrics.Bpcer.Total));

        Assert.Equal((4L, 4L), (metrics.ShadowAutomation.Count, metrics.ShadowAutomation.Total));
        Assert.Equal((0L, 4L), (metrics.Document.FalseRejects.Count, metrics.Document.FalseRejects.Total));
        Assert.Equal(run.Results.Policy.IdentifyingFields, ["surname", "givenNames", "birthDate", "documentNumber"]);
        Assert.Equal(run.Results.Thresholds, QualificationThresholds.From(run.Pipeline.ActivePolicy));

        var again = QualificationMetrics.Compute(run.Results.Cases, run.Results.ImpostorTrials, run.Results.Thresholds, run.Results.Confidence, run.Results.Models);
        Assert.Equal(JsonSerializer.Serialize(metrics, QualificationResults.JsonOptions), JsonSerializer.Serialize(again, QualificationResults.JsonOptions));
    }

    [Fact]
    public void Run_ACandidateModelIsScoredOnTheSamePictures_AndComparedAtTheSameAcceptances()
    {
        var paired = Case("g-p1").PairedMatch!;

        Assert.Equal(run.Pipeline.ActivePolicy.Match.Frames, paired.Reference.Count);
        Assert.Equal(paired.Reference.Count, paired.Candidate!.Count);
        Assert.Null(paired.CandidateCode);
        Assert.All(run.Results.ImpostorTrials, trial => Assert.NotNull(trial.Paired?.Candidate));
        Assert.NotNull(Case("a-p1").PairedLiveness?.Candidate);
        Assert.Equal(Case("g-p1").Presence!.LivenessFrames.Count, Case("g-p1").PairedLiveness!.Reference.Count);

        Assert.Contains(run.Results.Models, model => model.Role == BiometricModelRoles.FaceRecognitionCandidate && model.Loaded);
        Assert.Contains(run.Results.Models, model => model.Role == BiometricModelRoles.AntiSpoofingCandidate && model.Loaded);
        var comparisons = run.Results.Metrics.CandidateComparisons;
        Assert.Equal(["match", "liveness"], comparisons.Select(comparison => comparison.Measure));
        Assert.All(comparisons, comparison => Assert.Equal(comparison.ReferencePoint.Negatives, comparison.CandidatePoint.Negatives));
        Assert.All(comparisons, comparison => Assert.True(comparison.CandidatePoint.FalseAccepts <= comparison.ReferencePoint.FalseAccepts));
    }
}
