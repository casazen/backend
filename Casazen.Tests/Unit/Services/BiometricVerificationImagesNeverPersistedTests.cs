using System.Globalization;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Casazen.Tests.Unit.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T01, privacy rule (ADR-013-R2, AC-NF51-9, AC-NF51-12): the selfie, the photo of the document, the crops and the vectors are held
/// in memory only. After a verification that succeeds, one that fails and one that throws, none of their bytes appears in the logs, every array
/// the service held is zeroed, and the service has nothing that could write them (no database, no queue, no file) or return them.
/// </summary>
public class BiometricVerificationImagesNeverPersistedTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static DetectedFace Face(double areaRatio) => new(10, 10, 40, 40, 0.99f, new float[10], areaRatio);

    private sealed record Run(
        CapturingLogger<BiometricVerificationService> Log,
        ScriptedFaceDetector Detector,
        ScriptedFaceEmbedder Embedder,
        IReadOnlyList<string> ForbiddenInLogs,
        BiometricVerificationResult? Result);

    /// <summary>The text a log could hold if it wrote a picture or a vector: base64, hex, decimal lists, and the numbers of the vectors.</summary>
    private static IEnumerable<string> Forbidden(byte[] encoded, byte[] pixels, params float[][] vectors)
    {
        // Bytes from the middle of the compressed data (the signature of a PNG is the same for every picture).
        var fromFile = encoded.Skip(encoded.Length / 2).Take(16).ToArray();
        var fromPixels = pixels.Take(16).ToArray();
        yield return Convert.ToBase64String(encoded);
        foreach (var slice in new[] { fromFile, fromPixels })
        {
            yield return Convert.ToHexString(slice);
            yield return BitConverter.ToString(slice);
            yield return string.Join(",", slice);
            yield return string.Join(" ", slice);
        }

        foreach (var value in vectors.SelectMany(v => v))
        {
            yield return value.ToString("R", Invariant);
            yield return value.ToString("F6", Invariant);
        }
    }

    private static async Task<Run> Execute(
        string scenario,
        Func<float[], float[], (ScriptedFaceDetector Detector, ScriptedFaceEmbedder Embedder)> script,
        Func<byte[], string>? failureText = null)
    {
        var selfiePixels = TestImages.RandomRgb(48, 32, seed: 31);
        var documentPixels = TestImages.RandomRgb(64, 40, seed: 47);
        var selfiePng = TestImages.Png(48, 32, selfiePixels);
        var documentPng = TestImages.Png(64, 40, documentPixels);
        var selfieVector = new[] { 0.123457f, 0.654321f, 0.246810f, 0.135792f };
        var documentVector = new[] { 0.123457f, 0.654321f, 0.246810f, 0.135792f };

        // Taken before the run: the application zeroes the very arrays the test keeps.
        var forbidden = Forbidden(selfiePng, selfiePixels, selfieVector)
            .Concat(Forbidden(documentPng, documentPixels, documentVector))
            .Distinct()
            .ToList();

        var (detector, embedder) = script(selfieVector, documentVector);
        if (failureText is not null)
            embedder.FailOn = (2, new InvalidOperationException(failureText(selfiePng)));

        var antiSpoofing = new Mock<IAntiSpoofingService>();
        antiSpoofing
            .Setup(s => s.ScoreLivenessAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LivenessResult(true, 0.93));
        var log = new CapturingLogger<BiometricVerificationService>();
        var service = new BiometricVerificationService(
            antiSpoofing.Object, log, new FakeBiometricModelStore(), detector, embedder, Options.Create(new BiometricModelOptions()));

        var result = await service.VerifyAsync(new MemoryStream(selfiePng), new MemoryStream(documentPng), 0.85, 0.70);
        Assert.True(log.Entries.Count > 0, $"{scenario}: the service logged nothing, so the proof would be empty");
        return new Run(log, detector, embedder, forbidden, result);
    }

    private static void AssertNothingKept(string scenario, Run run)
    {
        var output = run.Log.AllOutput;
        foreach (var token in run.ForbiddenInLogs)
            Assert.DoesNotContain(token, output);

        // The exception itself is never handed to the logger: its message could carry what was being read.
        Assert.All(run.Log.Entries, entry => Assert.Null(entry.Exception));

        // Every picture and every vector the service held has been zeroed.
        Assert.NotEmpty(run.Detector.SeenPixels);
        foreach (var pixels in run.Detector.SeenPixels)
            Assert.True(pixels.All(b => b == 0), $"{scenario}: a decoded picture was left in memory");

        foreach (var vector in run.Embedder.Issued)
            Assert.True(vector.All(v => v == 0f), $"{scenario}: a face vector was left in memory");
    }

    [Fact]
    public async Task BiometricVerification_ImagesNeverPersisted_Test()
    {
        // Success: both faces found, the vectors compared, Accept.
        var success = await Execute(
            "success",
            (selfie, document) => (
                new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]),
                new ScriptedFaceEmbedder(selfie, document)));
        Assert.Equal(BiometricOutcome.Accept, success.Result!.Outcome);
        Assert.Equal(2, success.Embedder.Issued.Count);
        AssertNothingKept("success", success);

        // Failure: no face in the photo of the document, Reject.
        var failure = await Execute(
            "failure",
            (_, _) => (new ScriptedFaceDetector([Face(0.3)], []), new ScriptedFaceEmbedder()));
        Assert.Equal(BiometricOutcome.Reject, failure.Result!.Outcome);
        Assert.Equal(BiometricReasonCodes.NoFace, failure.Result.ReasonCode);
        AssertNothingKept("failure", failure);

        // Exception: the second vector cannot be made and the error text holds the whole selfie in base64, Escalate.
        var exception = await Execute(
            "exception",
            (selfie, document) => (
                new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]),
                new ScriptedFaceEmbedder(selfie, document)),
            failureText: selfiePng => "boom " + Convert.ToBase64String(selfiePng));
        Assert.Equal(BiometricOutcome.Escalate, exception.Result!.Outcome);
        Assert.Equal(BiometricReasonCodes.Error, exception.Result.ReasonCode);
        Assert.Single(exception.Embedder.Issued);
        AssertNothingKept("exception", exception);
    }

    [Fact]
    public void BiometricVerification_HasNothingThatCouldWriteAPicture()
    {
        // No database, no job queue, no e-mail queue, no object storage, no form file: the services only read streams and return numbers.
        var services = new[]
        {
            typeof(BiometricVerificationService), typeof(AntiSpoofingService), typeof(FaceDetector), typeof(FaceEmbedder),
            typeof(BiometricModelStore),
        };
        foreach (var service in services)
        {
            foreach (var constructor in service.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    var type = parameter.ParameterType;
                    Assert.False(typeof(DbContext).IsAssignableFrom(type), $"{service.Name} takes a database context");
                    Assert.DoesNotContain("Hangfire", type.Namespace ?? string.Empty, StringComparison.Ordinal);
                    Assert.DoesNotContain("Storage", type.Name, StringComparison.Ordinal);
                    Assert.DoesNotContain("Queue", type.Name, StringComparison.Ordinal);
                    Assert.DoesNotContain("FormFile", type.Name, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void BiometricVerificationResult_HoldsNumbersFlagsAndCodesOnly()
    {
        var allowed = new[] { typeof(bool), typeof(int), typeof(long), typeof(double), typeof(string), typeof(BiometricOutcome) };
        foreach (var property in typeof(BiometricVerificationResult).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.Contains(property.PropertyType, allowed);

        // The numbers the agent needs (E03/F04/US01/T01) are all there.
        foreach (var name in new[]
                 {
                     "LivenessScore", "FaceInSelfie", "FaceInDocument", "FaceAreaRatioSelfie", "Similarity", "ModelName", "ModelVersion",
                     "DurationMs",
                 })
            Assert.NotNull(typeof(BiometricVerificationResult).GetProperty(name));

        // And the liveness result is a score and a flag.
        Assert.Equal(2, typeof(LivenessResult).GetProperties().Length);
    }

    // ─── E73/F01/US02/T01: the evidence of the reviewer of the self-hosted closure (NF-195, ADR-013-R20) ─────────────────────────────────────────────

    [Fact]
    public async Task BiometricVerificationImagesNeverPersisted_FlagOff_NothingPersisted()
    {
        // Proof (privacy, AC1): with Features:CheckInIdentityReviewEvidence off a review keeps nothing, as before: the kept picture of the document goes, no evidence row, no
        // object written, whatever the store wired in the flow could do.
        await using var run = await EvidenceRun.CreateAsync(evidenceOn: false);

        var answer = await run.SubmitAsync();

        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        Assert.Empty(run.Storage.Inner.Objects);
        Assert.DoesNotContain(run.Storage.Operations, operation => operation.StartsWith("put:", StringComparison.Ordinal));
        Assert.Empty(await run.EvidenceRowsAsync());
    }

    [Fact]
    public async Task BiometricVerificationImagesNeverPersisted_ReviewEvidenceOn_OnlyCiphertextOfTheCropAndTheChosenFrames_BeforeTheDocumentGoes()
    {
        // Proof (privacy, AC1, AC3, AC5): with the flag on a review keeps the crop of the portrait and the frames the server chose, and nothing else: only ciphertext reaches the
        // bucket (no PNG, no JPEG signature), it decrypts to a PNG and to the JPEG frames received without their metadata (no vector, no score in them), the rows are linked to the
        // log row of the decision, and all of it is written BEFORE the kept picture of the document is deleted by the way to the operators.
        await using var run = await EvidenceRun.CreateAsync(evidenceOn: true);

        var answer = await run.SubmitAsync();

        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        var log = Assert.Single(await run.World.LogRowsAsync(run.Session));
        Assert.Equal(BiometricOutcome.Escalate, log.Outcome);
        var rows = await run.EvidenceRowsAsync();
        Assert.Equal(4, rows.Count);
        Assert.Single(rows, row => row.Kind == IdentityEvidenceKind.DocumentPortrait);
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        byte[] jpeg = [0xFF, 0xD8];
        var chosen = new[] { 0, 2, 3 }.Select(index => IdentityReviewEvidenceStoreTests.Strip(run.SubmittedFrames[index])).ToList();
        Assert.All(rows, row =>
        {
            Assert.Equal((log.Id, run.Session.Id), (row.VerificationLogId, row.CheckInSessionId));
            var stored = run.Storage.Inner.Objects[row.StorageKey];
            Assert.False(stored.Take(4).SequenceEqual(png), "a PNG reached the bucket in clear");
            Assert.False(stored.Take(2).SequenceEqual(jpeg), "a JPEG reached the bucket in clear");
            var plain = run.World.DataProtection.CreateProtector(IdentityReviewEvidenceStore.ContentPurpose, row.Id.ToString("N")).Unprotect(stored);
            if (row.Kind == IdentityEvidenceKind.DocumentPortrait)
                Assert.True(plain.Take(4).SequenceEqual(png), "the crop is not a PNG");
            else
                Assert.Contains(chosen, frame => frame.SequenceEqual(plain));
        });

        // Only the evidence is left in the bucket: the kept picture of the document was deleted, and after the evidence was written.
        Assert.Equal(rows.Select(row => row.StorageKey).Order(), run.Storage.Inner.Objects.Keys.Order());
        var lastEvidencePut = run.Storage.Operations.FindLastIndex(operation => operation.StartsWith("put:identity-evidence/", StringComparison.Ordinal));
        var documentDeleted = run.Storage.Operations.FindIndex(operation => operation == "delete:" + run.DocumentKey);
        Assert.True(lastEvidencePut >= 0 && documentDeleted > lastEvidencePut, string.Join(" | ", run.Storage.Operations));
        Assert.Equal([(run.Session.Id, VerificationAlertReasons.Review)], run.World.Alerts.Raised);
    }

    [Fact]
    public async Task BiometricVerificationImagesNeverPersisted_ReviewEvidenceOn_NothingForATicinoProperty_NorForAnApproval()
    {
        // Proof (privacy, AC2): a property that keeps no document keeps no evidence (ADR-013-R6), and an approval keeps none either: the guest is approved, nobody reviews.
        await using (var ticino = await EvidenceRun.CreateAsync(evidenceOn: true, keepsNoDocument: true))
        {
            var answer = await ticino.SubmitAsync();

            Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
            Assert.Empty(await ticino.EvidenceRowsAsync());
            Assert.Empty(ticino.Storage.Inner.Objects);
            Assert.Empty(ticino.Detector.Calls);
        }

        await using var approved = await EvidenceRun.CreateAsync(evidenceOn: true, allowApproval: true);
        var ok = await approved.SubmitAsync();

        Assert.Equal(IdentityVerificationGuestResults.Ok, ok.Result);
        Assert.Empty(await approved.EvidenceRowsAsync());
        Assert.Empty(approved.Storage.Inner.Objects);
        Assert.DoesNotContain(approved.Storage.Operations, operation => operation.StartsWith("put:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BiometricVerificationImagesNeverPersisted_AFailureOfTheEvidence_LeavesTheDecisionAndTheHandOffAsTheyAre()
    {
        // AC4: the bucket refuses the evidence; the review is the same (its row, its outcome code, the operators told once), no evidence row is left, and the kept picture of the
        // document is deleted as on every way to a person.
        await using var run = await EvidenceRun.CreateAsync(evidenceOn: true);
        run.Storage.FailEvidencePuts = true;

        var answer = await run.SubmitAsync();

        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        Assert.Equal(BiometricOutcome.Escalate, Assert.Single(await run.World.LogRowsAsync(run.Session)).Outcome);
        Assert.Equal(Casazen.Core.Identity.IdentityOutcomeCodes.Review, (await run.World.ReadBackAsync(run.Session.Id)).IdentityOutcomeCode);
        Assert.Equal([(run.Session.Id, VerificationAlertReasons.Review)], run.World.Alerts.Raised);
        Assert.Empty(await run.EvidenceRowsAsync());
        Assert.Empty(run.Storage.Inner.Objects);
    }

    /// <summary>
    /// One closure of the presence on the world of <see cref="PresenceClosureWorld"/> with the store of the evidence wired in the flow: the kept front of the document is a real
    /// picture (generated noise), the pipeline chose three frames, the detector of the store finds a portrait on any picture, and the bucket records the order of its operations.
    /// </summary>
    private sealed class EvidenceRun : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-evidence-closure-" + Guid.NewGuid().ToString("N"));

        private EvidenceRun(PresenceClosureWorld world, GuestCheckInSession session, string documentKey)
        {
            World = world;
            Session = session;
            DocumentKey = documentKey;
            Storage = new OrderedStorage(world.Storage);
            Detector.Fallback = _ => [new DetectedFace(60, 20, 40, 50, 0.99f, new float[10], 0.08)];
            Directory.CreateDirectory(_directory);
        }

        public PresenceClosureWorld World { get; }

        public GuestCheckInSession Session { get; }

        /// <summary>The key of the kept picture of the document (empty where nothing is kept).</summary>
        public string DocumentKey { get; }

        public OrderedStorage Storage { get; }

        public KeyedFaceDetector Detector { get; } = new();

        /// <summary>The JPEG of the frames sent, in their order.</summary>
        public List<byte[]> SubmittedFrames { get; } = [];

        public static async Task<EvidenceRun> CreateAsync(bool evidenceOn, bool keepsNoDocument = false, bool allowApproval = false)
        {
            var world = await PresenceClosureWorld.CreateAsync();
            world.Flags.Set(Casazen.Core.Features.FeatureFlags.CheckInIdentityReviewEvidence, evidenceOn);
            if (allowApproval)
                world.Segment = EvidenceBundles.Allowed;

            var session = await world.ReadyStayAsync(keepsNoDocument);

            // The kept front of the document becomes a picture the store can cut (the flow decrypts it from the bucket, as it was kept).
            var documentKey = world.Storage.Objects.Keys.SingleOrDefault() ?? string.Empty;
            if (documentKey.Length > 0)
            {
                world.Storage.Objects[documentKey] = world.DataProtection
                    .CreateProtector(IdentityVerificationFlowService.DocumentImageContentPurpose)
                    .Protect(TestImages.Png(200, 120, seed: 7));
            }

            world.Collector.Default = PresenceClosureKit.Perfect() with { Selection = new SelectedFrames([0, 2, 3], [0, 1, 2, 3]) };
            return new EvidenceRun(world, session, documentKey);
        }

        public async Task<PresenceStepResult> SubmitAsync()
        {
            var token = await World.IssueChallengeAsync(Session);
            var submission = Session.Booking.PropertyId == World.SwissPropertyId
                ? PresenceClosureKit.Submission(
                    PresenceClosureKit.NewKey(),
                    token,
                    documentPhoto: new MemoryStream(PresenceClosureKit.FrontPicture),
                    documentPhotoBack: new MemoryStream(PresenceClosureKit.BackPicture))
                : PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token);
            foreach (var frame in submission.Frames)
            {
                var copy = new MemoryStream();
                await frame.Content.CopyToAsync(copy);
                frame.Content.Position = 0;
                SubmittedFrames.Add(copy.ToArray());
            }

            return await Flow().SubmitPresenceSequenceAsync(Session, submission, CancellationToken.None);
        }

        public async Task<List<Casazen.Core.Entities.IdentityReviewEvidence>> EvidenceRowsAsync()
        {
            await using var read = World.NewContext();
            return await read.IdentityReviewEvidences.IgnoreQueryFilters().AsNoTracking().Where(row => row.BookingId == Session.BookingId).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await World.DisposeAsync();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        /// <summary>The flow of <see cref="PresenceClosureWorld.Flow"/>, with the store of the evidence and the bucket that records its operations.</summary>
        private IdentityVerificationFlowService Flow()
        {
            var store = new IdentityReviewEvidenceStore(
                World.Db,
                Storage,
                World.DataProtection,
                World.Flags,
                Options.Create(World.Limits),
                Options.Create(new BiometricModelOptions()),
                Detector,
                World.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityReviewEvidenceStore>.Instance);
            return new IdentityVerificationFlowService(
                World.Db,
                Mock.Of<IIdentityVerificationAgent>(),
                World.Alerts,
                World.AccessCodes.Object,
                Mock.Of<Casazen.Infrastructure.Email.IEmailQueue>(),
                Mock.Of<IMrzExtractionService>(),
                Mock.Of<IDocumentOcrService>(),
                new StayGuestService(World.Db, Mock.Of<IAlloggiatiCodeTableService>()),
                Storage,
                World.DataProtection,
                World.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityVerificationFlowService>.Instance,
                domainEvents: World.Events.Object,
                legalTexts: World.Registry.Object,
                featureFlags: World.Flags,
                identityPolicy: PresenceClosureKit.Policy(_directory),
                biometricModels: new FakeBiometricModelStore(World.ModelsReady),
                identityOptions: Options.Create(World.Limits),
                presenceIssuer: new PresenceChallengeIssuer(),
                formats: PresenceClosureKit.Formats(),
                frameReader: new FrameSequenceReader(
                    Options.Create(World.Limits),
                    Options.Create(new BiometricModelOptions()),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<FrameSequenceReader>.Instance),
                evidenceCollector: World.Collector,
                segments: World.Segments.Object,
                decisionEngine: new IdentityDecisionEngine(),
                reviewEvidence: store);
        }
    }

    /// <summary>The bucket of the world, recording its writes and deletions in order (<c>put:key</c>, <c>delete:key</c>); it can refuse the writes of the evidence.</summary>
    private sealed class OrderedStorage(PresenceMemoryStorage inner) : IFileStorage
    {
        public PresenceMemoryStorage Inner { get; } = inner;

        public List<string> Operations { get; } = [];

        public bool FailEvidencePuts { get; set; }

        public Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            Operations.Add("put:" + key);
            if (FailEvidencePuts && key.StartsWith(IdentityReviewEvidenceStore.StorageKeyPrefix, StringComparison.Ordinal))
                return Task.FromException(new IOException("The bucket refuses the object."));

            return Inner.PutAsync(bucket, key, content, contentType, cancellationToken);
        }

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) => Inner.OpenReadAsync(bucket, key, cancellationToken);

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) => Inner.ExistsAsync(bucket, key, cancellationToken);

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
        {
            Operations.Add("delete:" + key);
            return Inner.DeleteAsync(bucket, key, cancellationToken);
        }

        public string GetPublicUrl(string key) => Inner.GetPublicUrl(key);

        public string? TryGetPublicKey(string url) => Inner.TryGetPublicKey(url);

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Inner.GetSignedReadUrlAsync(key, lifetime, downloadFileName, cancellationToken);
    }
}
