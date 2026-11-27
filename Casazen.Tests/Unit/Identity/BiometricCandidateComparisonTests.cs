using System.Reflection;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E72/F01/US02/T03 (NF-193, decision D6): the comparison of the optional second candidates with the reference models on the same cases, and the proof that a candidate never decides. A
/// candidate is a COPY of a bundled model in a temporary folder, with its real SHA-256 (no weight file is added to the repository), so that the reference and the candidate are the same
/// weights and must agree to the last digits, and a candidate described with another normalization must not. The faces come from doubles and the pictures are noise generated in code.
/// </summary>
public sealed class BiometricCandidateComparisonTests : IDisposable
{
    private const int Width = 160;
    private const int Height = 120;

    private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cz-candidates-" + Guid.NewGuid().ToString("N"));

    public BiometricCandidateComparisonTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    // ─── The proof of the brief ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (security, decision D6): a candidate never decides live. Nothing the verification flow is made of, down its constructors and fields (through every implementation of an interface or
    /// of an abstract type it takes, wrappers and lazy values included), takes the comparison or its interface; no service the identity verification registers takes it either. The guard is not
    /// blind: a flow that takes it through two levels, or as a lazy value, is found by the same walk.
    /// </summary>
    [Fact]
    public void IdentityVerificationFlowService_DoesNotDependOnCandidateComparison()
    {
        Type[] comparison = [typeof(IBiometricCandidateComparison), typeof(BiometricCandidateComparison)];

        var reachable = Dependencies(typeof(IdentityVerificationFlowService));

        // The walk goes down to the models (so it would meet the comparison if the flow, or what it takes, took it).
        Assert.Contains(typeof(IIdentityVerificationAgent), reachable);
        Assert.Contains(typeof(IBiometricModelStore), reachable);
        Assert.Contains(typeof(IFaceEmbedder), reachable);
        Assert.DoesNotContain(reachable, type => comparison.Any(candidate => candidate.IsAssignableFrom(type)));
        var flowParameters = typeof(IdentityVerificationFlowService).GetConstructors().SelectMany(constructor => constructor.GetParameters());
        Assert.DoesNotContain(flowParameters, parameter => comparison.Any(candidate => candidate.IsAssignableFrom(parameter.ParameterType)));

        Assert.Contains(typeof(IBiometricCandidateComparison), Dependencies(typeof(PlantedFlow)));
        Assert.Contains(typeof(IBiometricCandidateComparison), Dependencies(typeof(PlantedLazyFlow)));
    }

    [Fact]
    public void Registration_TheComparisonIsRegisteredForTheRunnerAsASingleton_AndNoRegisteredServiceTakesIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSelfHostedIdentityVerification();

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IBiometricCandidateComparison));
        Assert.Equal(typeof(BiometricCandidateComparison), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        // Of everything the identity verification registers, nothing has the comparison among the parameters of its constructor.
        var takers = services
            .Select(service => service.ImplementationType)
            .OfType<Type>()
            .Where(type => type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Any(parameter => typeof(IBiometricCandidateComparison).IsAssignableFrom(parameter.ParameterType)))
            .ToList();
        Assert.Empty(takers);
    }

    // ─── Acceptance criterion 3: paired scores on the same inputs ────────────────────────────────────────

    [Fact]
    public async Task CompareAsync_AMatchCase_GivesTheSimilaritiesOfTheReferenceAndOfTheCandidate_OnTheSameInputs()
    {
        // The candidate is a copy of the bundled SFace: the same weights on the same faces, so every similarity is the same.
        var options = BundledBiometricModels.PublishedOptions();
        options.FaceRecognitionCandidate = CandidateFrom<FaceRecognitionCandidateOptions>(options.FaceRecognition, "recognition-copy.onnx");
        using var store = await BundledBiometricModels.LoadAsync(options);
        using var scene = MatchScene(frames: 3, withoutFace: 1);
        var embedder = new RecordingEmbedder(new FaceEmbedder(store, Options.Create(options)));

        var results = await Comparison(store, scene.Detector, embedder, options).CompareAsync([scene.Case("match-1")], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(("match-1", CandidateCaseKind.Match), (result.CaseId, result.Kind));
        var reference = result.Reference;
        var candidate = Assert.IsType<CandidateModelRun>(result.Candidate);
        Assert.True(reference.Ran);
        Assert.True(candidate.Ran);
        Assert.Equal(BiometricModelRoles.FaceRecognition, reference.Role);
        Assert.Equal(BiometricModelRoles.FaceRecognitionCandidate, candidate.Role);
        Assert.Equal((options.FaceRecognition.Name, options.FaceRecognition.Version), (reference.Name, reference.Version));
        Assert.Equal((options.FaceRecognitionCandidate.Name, options.FaceRecognitionCandidate.Version), (candidate.Name, candidate.Version));
        Assert.Equal(options.FaceRecognition.Sha256, reference.WeightHash, ignoreCase: true);
        Assert.Equal(options.FaceRecognitionCandidate.Sha256, candidate.WeightHash, ignoreCase: true);

        // One similarity per frame, in the order of the frames, on the scale of the check-in; the frame with no face is 0 for both.
        Assert.Equal(3, reference.Scores.Count);
        Assert.Equal(3, candidate.Scores.Count);
        Assert.All(reference.Scores.Concat(candidate.Scores), score => Assert.InRange(score, 0.0, 1.0));
        Assert.Equal(0.0, reference.Scores[2]);
        Assert.Equal(0.0, candidate.Scores[2]);
        for (var index = 0; index < 3; index++)
            Assert.Equal(reference.Scores[index], candidate.Scores[index], 6);

        // And the reference is what the check-in computes: the cosine of the two vectors of its own embedder, clamped to 0..1.
        var own = new FaceEmbedder(store, Options.Create(options));
        var portrait = own.Embed(scene.Document, scene.PortraitFace, CancellationToken.None);
        for (var index = 0; index < 2; index++)
        {
            var frame = own.Embed(scene.Frames[index], scene.FrameFace, CancellationToken.None);
            Assert.Equal(Math.Clamp(EmbeddingMath.CosineSimilarity(portrait, frame), 0.0, 1.0), reference.Scores[index], 6);
        }

        // Both took time, and the vectors the reference made (the portrait and the two frames with a face) were zeroed.
        Assert.True(reference.LatencyMs > 0);
        Assert.True(candidate.LatencyMs > 0);
        Assert.Equal(3, embedder.Issued.Count);
        Assert.All(embedder.Issued, vector => Assert.All(vector, value => Assert.Equal(0f, value)));
    }

    [Fact]
    public async Task CompareAsync_ALivenessCase_GivesTheScoreOfTheReferenceAndOfTheCandidate_OnTheSameFrame()
    {
        var options = BundledBiometricModels.PublishedOptions();
        options.AntiSpoofingCandidate = CandidateFrom<AntiSpoofingCandidateOptions>(options.AntiSpoofing, "antispoofing-copy.onnx");
        using var store = await BundledBiometricModels.LoadAsync(options);
        var face = SequenceTestFrames.Face(Width, Height);
        var frame = TestJpeg.Noise(Width, Height, seed: 9);
        var detector = new ScriptedFaceDetector([face], [face], [face]);

        var results = await Comparison(store, detector, new FaceEmbedder(store, Options.Create(options)), options).CompareAsync([new LivenessCase("frame-1", frame)], CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(("frame-1", CandidateCaseKind.Liveness), (result.CaseId, result.Kind));
        var candidate = Assert.IsType<CandidateModelRun>(result.Candidate);
        Assert.Equal(BiometricModelRoles.AntiSpoofing, result.Reference.Role);
        Assert.Equal(BiometricModelRoles.AntiSpoofingCandidate, candidate.Role);
        Assert.True(result.Reference.Ran);
        Assert.True(candidate.Ran);
        var referenceScore = Assert.Single(result.Reference.Scores);
        var candidateScore = Assert.Single(candidate.Scores);
        Assert.InRange(referenceScore, 1e-12, 1.0);
        Assert.Equal(referenceScore, candidateScore, 6);
        Assert.True(result.Reference.LatencyMs > 0);
        Assert.True(candidate.LatencyMs > 0);

        // The reference is the service of the check-in on the same bytes (the third answer of the detector double).
        var service = new AntiSpoofingService(NullLogger<AntiSpoofingService>.Instance, store, detector, Options.Create(options));
        using var picture = new MemoryStream(frame, writable: false);
        Assert.Equal((await service.ScoreLivenessAsync(picture)).Score, referenceScore, 6);
    }

    [Fact]
    public async Task CompareAsync_ACandidateWithAnotherNormalization_IsRunWithItsOwn_AndDiffersFromTheReference()
    {
        // The same weights described with another normalization (a mean and a divisor of 127.5) and another crop (1.5 instead of 2.7): not the same numbers, which proves the description counts.
        var options = BundledBiometricModels.PublishedOptions();
        var recognition = CandidateFrom<FaceRecognitionCandidateOptions>(options.FaceRecognition, "recognition-other.onnx");
        recognition.Mean = 127.5;
        recognition.Std = 127.5;
        options.FaceRecognitionCandidate = recognition;
        var antiSpoofing = CandidateFrom<AntiSpoofingCandidateOptions>(options.AntiSpoofing, "antispoofing-other.onnx");
        antiSpoofing.CropScale = 1.5;
        options.AntiSpoofingCandidate = antiSpoofing;
        using var store = await BundledBiometricModels.LoadAsync(options);
        using var scene = MatchScene(frames: 3, withoutFace: 0);

        // A small face: the crop of the face is enlarged up to the scale and no further than the picture, so a large face would leave the two scales the same crop.
        var face = SequenceTestFrames.Face(Width, Height, widthShare: 0.2);

        var matches = await Comparison(store, scene.Detector, new FaceEmbedder(store, Options.Create(options)), options).CompareAsync([scene.Case("match")], CancellationToken.None);
        var lives = await Comparison(store, new ScriptedFaceDetector([face], [face]), new FaceEmbedder(store, Options.Create(options)), options)
            .CompareAsync([new LivenessCase("frame", TestJpeg.Noise(Width, Height, seed: 21))], CancellationToken.None);

        var match = Assert.Single(matches);
        var matchCandidate = Assert.IsType<CandidateModelRun>(match.Candidate);
        Assert.True(matchCandidate.Ran);
        Assert.Contains(Enumerable.Range(0, 3), index => Math.Abs(match.Reference.Scores[index] - matchCandidate.Scores[index]) > 1e-6);
        var live = Assert.Single(lives);
        var liveCandidate = Assert.IsType<CandidateModelRun>(live.Candidate);
        Assert.True(liveCandidate.Ran);
        Assert.True(Math.Abs(live.Reference.Scores[0] - liveCandidate.Scores[0]) > 1e-6);
    }

    [Fact]
    public async Task CompareAsync_WithNoCandidateLoaded_GivesTheReferenceAlone()
    {
        var options = BundledBiometricModels.PublishedOptions();
        using var store = await BundledBiometricModels.LoadAsync(options);
        using var scene = MatchScene(frames: 2, withoutFace: 0);

        // The pictures a liveness case is decoded into are new objects: the detector double finds the face of the size of the frames in them.
        var face = SequenceTestFrames.Face(Width, Height);
        scene.Detector.Fallback = image => image.Width == Width && image.Height == Height ? [face] : [];
        var results = await Comparison(store, scene.Detector, new FaceEmbedder(store, Options.Create(options)), options)
            .CompareAsync([scene.Case("match"), new LivenessCase("frame", TestJpeg.Noise(Width, Height, seed: 3))], CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.True(result.Reference.Ran);
            Assert.Null(result.Candidate);
        });
    }

    // ─── The window between «the store is ready» and «the store has looked at the candidates» ────────────

    [Fact]
    public async Task CompareAsync_ACandidateTheStoreHasNotLookedAtYet_IsCandidateLoading_NotAMissingOne_AndTheReferenceRan()
    {
        // The store publishes «ready» as soon as the three models are loaded and loads the candidates after them: for a moment it is ready, the candidates are configured and its snapshot lists none of them.
        var options = BundledBiometricModels.PublishedOptions();
        options.FaceRecognitionCandidate = CandidateFrom<FaceRecognitionCandidateOptions>(options.FaceRecognition, "recognition-copy.onnx");
        options.AntiSpoofingCandidate = CandidateFrom<AntiSpoofingCandidateOptions>(options.AntiSpoofing, "antispoofing-copy.onnx");
        using var loaded = await BundledBiometricModels.LoadAsync(options);
        var early = new StoreThatHasNotLookedAtTheCandidates(loaded);
        using var scene = MatchScene(frames: 2, withoutFace: 0);
        var face = SequenceTestFrames.Face(Width, Height);
        scene.Detector.Fallback = image => image.Width == Width && image.Height == Height ? [face] : [];
        var cases = new CandidateCase[] { scene.Case("match"), new LivenessCase("frame", TestJpeg.Noise(Width, Height, seed: 3)) };

        var results = await Comparison(early, scene.Detector, new FaceEmbedder(loaded, Options.Create(options)), options).CompareAsync(cases, CancellationToken.None);

        // The reference ran, and the candidate of each role is a run that says why it has no scores: not a null, which would be «no candidate».
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Reference.Ran));
        var recognition = Assert.IsType<CandidateModelRun>(results[0].Candidate);
        var presence = Assert.IsType<CandidateModelRun>(results[1].Candidate);
        Assert.Equal((BiometricModelRoles.FaceRecognitionCandidate, CandidateRunCodes.CandidateLoading), (recognition.Role, recognition.FailureCode));
        Assert.Equal((BiometricModelRoles.AntiSpoofingCandidate, CandidateRunCodes.CandidateLoading), (presence.Role, presence.FailureCode));
        Assert.All(new[] { recognition, presence }, run =>
        {
            Assert.False(run.Ran);
            Assert.Empty(run.Scores);
            Assert.Null(run.WeightHash);
        });

        // Who it is: the name and the version of what is configured (its weights are not proved yet).
        Assert.Equal((options.FaceRecognitionCandidate.Name, options.FaceRecognitionCandidate.Version), (recognition.Name, recognition.Version));
        Assert.Equal((options.AntiSpoofingCandidate.Name, options.AntiSpoofingCandidate.Version), (presence.Name, presence.Version));

        // The same cases once the store has looked at the candidates (the real store, which loaded them): both candidates ran.
        var settled = await Comparison(loaded, scene.Detector, new FaceEmbedder(loaded, Options.Create(options)), options).CompareAsync(cases, CancellationToken.None);

        Assert.All(settled, result => Assert.True(Assert.IsType<CandidateModelRun>(result.Candidate).Ran));
    }

    [Fact]
    public async Task CompareAsync_ACandidateThatIsNotConfigured_IsNull_WhetherOrNotTheStoreHasLookedAtTheCandidatesYet()
    {
        // Nothing is configured: the runner must tell this from a candidate that is loading, so the answer stays «no candidate» (null). A configured candidate the store looked at and could not load is null
        // too, with its code in the snapshot (CompareAsync_ACandidateWithAnotherHash_IsNotLoaded_AndTheComparisonIsTheReferenceAlone).
        var options = BundledBiometricModels.PublishedOptions();
        using var loaded = await BundledBiometricModels.LoadAsync(options);
        using var scene = MatchScene(frames: 2, withoutFace: 0);
        var face = SequenceTestFrames.Face(Width, Height);
        scene.Detector.Fallback = image => image.Width == Width && image.Height == Height ? [face] : [];
        var cases = new CandidateCase[] { scene.Case("match"), new LivenessCase("frame", TestJpeg.Noise(Width, Height, seed: 3)) };

        var early = await Comparison(new StoreThatHasNotLookedAtTheCandidates(loaded), scene.Detector, new FaceEmbedder(loaded, Options.Create(options)), options).CompareAsync(cases, CancellationToken.None);
        var settled = await Comparison(loaded, scene.Detector, new FaceEmbedder(loaded, Options.Create(options)), options).CompareAsync(cases, CancellationToken.None);

        Assert.Equal(4, early.Count + settled.Count);
        Assert.All(early.Concat(settled), result =>
        {
            Assert.True(result.Reference.Ran);
            Assert.Null(result.Candidate);
        });
    }

    // ─── Acceptance criterion 2: a wrong hash ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompareAsync_ACandidateWithAnotherHash_IsNotLoaded_AndTheComparisonIsTheReferenceAlone()
    {
        var options = BundledBiometricModels.PublishedOptions();
        var candidate = CandidateFrom<FaceRecognitionCandidateOptions>(options.FaceRecognition, "recognition-wrong-hash.onnx");
        candidate.Sha256 = new string('A', 64);
        options.FaceRecognitionCandidate = candidate;
        using var store = await BundledBiometricModels.LoadAsync(options);
        using var scene = MatchScene(frames: 3, withoutFace: 0);

        var results = await Comparison(store, scene.Detector, new FaceEmbedder(store, Options.Create(options)), options).CompareAsync([scene.Case("match")], CancellationToken.None);

        Assert.True(store.IsAvailable);
        Assert.Equal("hash_mismatch", store.Snapshot.Candidates.Single(info => info.Role == BiometricModelRoles.FaceRecognitionCandidate).FailureCode);
        Assert.Null(store.FaceRecognitionCandidate);
        var result = Assert.Single(results);
        Assert.True(result.Reference.Ran);
        Assert.Null(result.Candidate);
    }

    // ─── The reference is not available, there is no portrait, a case fails ──────────────────────────────

    [Fact]
    public async Task CompareAsync_WhenTheReferenceModelsAreNotAvailable_EveryCaseIsACode_NeverAnException()
    {
        var options = new BiometricModelOptions();
        using var scene = MatchScene(frames: 2, withoutFace: 0);
        var comparison = Comparison(new FakeBiometricModelStore(available: false), scene.Detector, new ThrowingEmbedder(), options);

        var results = await comparison.CompareAsync([scene.Case("match"), new LivenessCase("frame", TestJpeg.Noise(Width, Height, seed: 3))], CancellationToken.None);

        Assert.Equal(["match", "frame"], results.Select(result => result.CaseId));
        Assert.All(results, result =>
        {
            Assert.False(result.Reference.Ran);
            Assert.Equal(CandidateRunCodes.Unavailable, result.Reference.FailureCode);
            Assert.Empty(result.Reference.Scores);
            Assert.Null(result.Candidate);
        });
        Assert.Equal(options.FaceRecognition.Name, results[0].Reference.Name);
        Assert.Equal(options.AntiSpoofing.Name, results[1].Reference.Name);
    }

    [Fact]
    public async Task CompareAsync_ADocumentWithNoFace_IsNoFaceForBothModels_AndACaseThatFailsIsACode_ThatDoesNotStopTheOthers()
    {
        var options = BundledBiometricModels.PublishedOptions();
        options.FaceRecognitionCandidate = CandidateFrom<FaceRecognitionCandidateOptions>(options.FaceRecognition, "recognition-copy.onnx");
        using var store = await BundledBiometricModels.LoadAsync(options);
        var detector = new KeyedFaceDetector();
        using var noPortrait = MatchScene(frames: 2, withoutFace: 0, portrait: false, detector);
        using var broken = MatchScene(frames: 2, withoutFace: 0, portrait: true, detector);
        using var good = MatchScene(frames: 2, withoutFace: 0, portrait: true, detector);

        // A picture that was disposed has no pixels: reading it fails, and says so with a code, not with the message of the exception.
        broken.Frames[0].Dispose();

        var results = await Comparison(store, detector, new FaceEmbedder(store, Options.Create(options)), options)
            .CompareAsync([noPortrait.Case("no-portrait"), broken.Case("broken"), good.Case("good")], CancellationToken.None);

        Assert.Equal(["no-portrait", "broken", "good"], results.Select(result => result.CaseId));
        Assert.Equal(CandidateRunCodes.NoFace, results[0].Reference.FailureCode);
        Assert.Equal(CandidateRunCodes.NoFace, Assert.IsType<CandidateModelRun>(results[0].Candidate).FailureCode);
        Assert.Equal(CandidateRunCodes.Error, results[1].Reference.FailureCode);
        Assert.Empty(results[1].Reference.Scores);
        Assert.True(results[2].Reference.Ran);
        Assert.True(Assert.IsType<CandidateModelRun>(results[2].Candidate).Ran);
    }

    [Fact]
    public async Task CompareAsync_WhenTheCallerCancels_IsTheCancellation_AndNothingIsCompared()
    {
        var options = BundledBiometricModels.PublishedOptions();
        using var store = await BundledBiometricModels.LoadAsync(options);
        using var scene = MatchScene(frames: 2, withoutFace: 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Comparison(store, scene.Detector, new FaceEmbedder(store, Options.Create(options)), options).CompareAsync([scene.Case("match")], new CancellationToken(canceled: true)));
        Assert.Empty(scene.Detector.Calls);
    }

    // ─── The cases and the class itself ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Cases_NeedAnIdentifierAndTheirPictures()
    {
        using var picture = TestPictures.Noise(Width, Height, 1);

        Assert.Throws<ArgumentException>(() => new MatchCase(" ", picture, [picture]));
        Assert.Throws<ArgumentException>(() => new MatchCase("match", picture, []));
        Assert.Throws<ArgumentNullException>(() => new MatchCase("match", null!, [picture]));
        Assert.Throws<ArgumentException>(() => new LivenessCase("", [1, 2, 3]));
        Assert.Throws<ArgumentException>(() => new LivenessCase("frame", []));
        Assert.Throws<ArgumentNullException>(() => new LivenessCase("frame", null!));
        Assert.Equal(CandidateCaseKind.Match, new MatchCase("match", picture, [picture]).Kind);
        Assert.Equal(CandidateCaseKind.Liveness, new LivenessCase("frame", [1, 2, 3]).Kind);
    }

    [Fact]
    public async Task CompareAsync_NothingToCompare_IsNothing_AndAMissingCaseIsAnArgumentException()
    {
        var comparison = Comparison(new FakeBiometricModelStore(available: false), new KeyedFaceDetector(), new ThrowingEmbedder(), new BiometricModelOptions());

        Assert.Empty(await comparison.CompareAsync([], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => comparison.CompareAsync([null!], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => comparison.CompareAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void BiometricCandidateComparison_HasNoLogger_SoNoNumberOfACaseCanReachALog()
    {
        var constructor = Assert.Single(typeof(BiometricCandidateComparison).GetConstructors());
        Assert.Equal(
            [typeof(IBiometricModelStore), typeof(IFaceDetector), typeof(IFaceEmbedder), typeof(IOptions<BiometricModelOptions>)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(BiometricCandidateComparison).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static),
            field => typeof(ILogger).IsAssignableFrom(field.FieldType));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static BiometricCandidateComparison Comparison(IBiometricModelStore store, IFaceDetector detector, IFaceEmbedder embedder, BiometricModelOptions options)
    {
        return new BiometricCandidateComparison(store, detector, embedder, Options.Create(options));
    }

    /// <summary>A copy of a bundled model in the temporary folder, described as a candidate: its own name, the path of the copy and the hash it really has (the one of the original).</summary>
    private T CandidateFrom<T>(ModelFileOptions bundled, string fileName)
        where T : ModelFileOptions, new()
    {
        var target = System.IO.Path.Combine(_folder, fileName);
        File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, bundled.Path), target);
        return new T
        {
            Name = bundled.Name + " (copy)",
            Version = "test",
            License = bundled.License,
            Provenance = "A copy of the bundled model, for the tests of the second-candidate slot; no weight is added to the repository.",
            Path = target,
            Sha256 = bundled.Sha256,
        };
    }

    /// <summary>The picture of a document and frames, with the faces the detector double finds in them.</summary>
    private sealed class Scene : IDisposable
    {
        public required RasterImage Document { get; init; }

        public required IReadOnlyList<RasterImage> Frames { get; init; }

        public required KeyedFaceDetector Detector { get; init; }

        public required DetectedFace PortraitFace { get; init; }

        public required DetectedFace FrameFace { get; init; }

        public MatchCase Case(string id) => new(id, Document, Frames);

        public void Dispose()
        {
            Document.Dispose();
            foreach (var frame in Frames)
                frame.Dispose();
        }
    }

    /// <summary>
    /// A document and <paramref name="frames"/> frames; the document has a face unless <paramref name="portrait"/> is false, and no face is found in the last <paramref name="withoutFace"/> frames.
    /// The faces are known to <paramref name="detector"/> (a new one when none is given).
    /// </summary>
    private static Scene MatchScene(int frames, int withoutFace, bool portrait = true, KeyedFaceDetector? detector = null)
    {
        detector ??= new KeyedFaceDetector();
        var document = TestPictures.Noise(Width, Width, seed: 1);
        var portraitFace = SequenceTestFrames.Face(Width, Width);
        if (portrait)
            detector.Add(document, portraitFace);

        var frameFace = SequenceTestFrames.Face(Width, Height);
        var pictures = new List<RasterImage>(frames);
        for (var index = 0; index < frames; index++)
        {
            var picture = TestPictures.Noise(Width, Height, seed: 100 + index);
            pictures.Add(picture);
            if (index < frames - withoutFace)
                detector.Add(picture, frameFace);
        }

        return new Scene { Document = document, Frames = pictures, Detector = detector, PortraitFace = portraitFace, FrameFace = frameFace };
    }

    /// <summary>Every type the root is made of, down its constructors and fields, through every implementation of an interface or an abstract type of the application, wrappers and lazy values unwrapped.</summary>
    private static HashSet<Type> Dependencies(Type root)
    {
        var implementations = new[] { typeof(IdentityVerificationFlowService).Assembly, typeof(IBiometricVerificationService).Assembly, typeof(IdentityVerificationServiceCollectionExtensions).Assembly }
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .ToList();
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([root]);
        while (queue.Count > 0)
        {
            foreach (var part in Parts(queue.Dequeue()))
            {
                if (!seen.Add(part) || part.Namespace is not { } name || !name.StartsWith("Casazen", StringComparison.Ordinal))
                    continue;

                if (part.IsInterface || part.IsAbstract)
                {
                    foreach (var implementation in implementations.Where(type => part.IsAssignableFrom(type)))
                        queue.Enqueue(implementation);
                }

                foreach (var constructor in part.GetConstructors(all))
                {
                    foreach (var parameter in constructor.GetParameters())
                        queue.Enqueue(parameter.ParameterType);
                }

                foreach (var field in part.GetFields(all))
                    queue.Enqueue(field.FieldType);
            }
        }

        return seen;
    }

    /// <summary>The type and what it is made of: the element of an array or a reference and the arguments of a generic type, followed down.</summary>
    private static IEnumerable<Type> Parts(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var part in Parts(type.GetElementType()!))
                yield return part;
        }

        if (type.IsGenericType)
        {
            foreach (var part in type.GetGenericArguments().SelectMany(Parts))
                yield return part;
        }
    }

    /// <summary>A flow that takes a service that takes the comparison: two levels down.</summary>
    private sealed class PlantedFlow(PlantedService inner)
    {
        public override string ToString() => inner.ToString() ?? string.Empty;
    }

    private sealed class PlantedService(IBiometricCandidateComparison comparison)
    {
        public override string ToString() => comparison.ToString() ?? string.Empty;
    }

    /// <summary>A flow that takes the comparison as a lazy value.</summary>
    private sealed class PlantedLazyFlow(Lazy<IBiometricCandidateComparison> comparison)
    {
        public override string ToString() => comparison.Value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// A store as the real one is for a moment after it publishes «ready» and before it has looked at the candidates (it loads them after the three models): the three models are there, its snapshot lists no
    /// candidate and none is loaded.
    /// </summary>
    private sealed class StoreThatHasNotLookedAtTheCandidates(IBiometricModelStore inner) : IBiometricModelStore
    {
        public BiometricModelsSnapshot Snapshot => inner.Snapshot with { Candidates = [] };

        public bool IsAvailable => inner.IsAvailable;

        public LoadedBiometricModel? FaceDetector => inner.FaceDetector;

        public LoadedBiometricModel? AntiSpoofing => inner.AntiSpoofing;

        public LoadedBiometricModel? FaceRecognition => inner.FaceRecognition;
    }

    /// <summary>An embedder that keeps what it hands out (the very arrays the application holds), to prove the application zeroes them, and lets the real one do the work.</summary>
    private sealed class RecordingEmbedder(IFaceEmbedder inner) : IFaceEmbedder
    {
        public List<float[]> Issued { get; } = [];

        public float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken)
        {
            var vector = inner.Embed(image, face, cancellationToken);
            Issued.Add(vector);
            return vector;
        }
    }

    /// <summary>An embedder that must not be asked.</summary>
    private sealed class ThrowingEmbedder : IFaceEmbedder
    {
        public float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The embedder must not be asked: the models are not available.");
    }
}
