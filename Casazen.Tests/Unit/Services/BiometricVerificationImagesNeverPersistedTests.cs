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

    private static IEnumerable<string> Forbidden(byte[] encoded, byte[] pixels, params float[][] vectors)
    {
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

        Assert.All(run.Log.Entries, entry => Assert.Null(entry.Exception));

        Assert.NotEmpty(run.Detector.SeenPixels);
        foreach (var pixels in run.Detector.SeenPixels)
            Assert.True(pixels.All(b => b == 0), $"{scenario}: a decoded picture was left in memory");

        foreach (var vector in run.Embedder.Issued)
            Assert.True(vector.All(v => v == 0f), $"{scenario}: a face vector was left in memory");
    }

    [Fact]
    public async Task BiometricVerification_ImagesNeverPersisted_Test()
    {
        var success = await Execute(
            "success",
            (selfie, document) => (
                new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]),
                new ScriptedFaceEmbedder(selfie, document)));
        Assert.Equal(BiometricOutcome.Accept, success.Result!.Outcome);
        Assert.Equal(2, success.Embedder.Issued.Count);
        AssertNothingKept("success", success);

        var failure = await Execute(
            "failure",
            (_, _) => (new ScriptedFaceDetector([Face(0.3)], []), new ScriptedFaceEmbedder()));
        Assert.Equal(BiometricOutcome.Reject, failure.Result!.Outcome);
        Assert.Equal(BiometricReasonCodes.NoFace, failure.Result.ReasonCode);
        AssertNothingKept("failure", failure);

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

        foreach (var name in new[]
                 {
                     "LivenessScore", "FaceInSelfie", "FaceInDocument", "FaceAreaRatioSelfie", "Similarity", "ModelName", "ModelVersion",
                     "DurationMs",
                 })
            Assert.NotNull(typeof(BiometricVerificationResult).GetProperty(name));

        Assert.Equal(2, typeof(LivenessResult).GetProperties().Length);
    }

    [Fact]
    public async Task BiometricVerificationImagesNeverPersisted_FlagOff_NothingPersisted()
    {
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

        Assert.Equal(rows.Select(row => row.StorageKey).Order(), run.Storage.Inner.Objects.Keys.Order());
        var lastEvidencePut = run.Storage.Operations.FindLastIndex(operation => operation.StartsWith("put:identity-evidence/", StringComparison.Ordinal));
        var documentDeleted = run.Storage.Operations.FindIndex(operation => operation == "delete:" + run.DocumentKey);
        Assert.True(lastEvidencePut >= 0 && documentDeleted > lastEvidencePut, string.Join(" | ", run.Storage.Operations));
        Assert.Equal([(run.Session.Id, VerificationAlertReasons.Review)], run.World.Alerts.Raised);
    }

    [Fact]
    public async Task BiometricVerificationImagesNeverPersisted_ReviewEvidenceOn_NothingForATicinoProperty_NorForAnApproval()
    {
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

        public string DocumentKey { get; }

        public OrderedStorage Storage { get; }

        public KeyedFaceDetector Detector { get; } = new();

        public List<byte[]> SubmittedFrames { get; } = [];

        public static async Task<EvidenceRun> CreateAsync(bool evidenceOn, bool keepsNoDocument = false, bool allowApproval = false)
        {
            var world = await PresenceClosureWorld.CreateAsync();
            world.Flags.Set(Casazen.Core.Features.FeatureFlags.CheckInIdentityReviewEvidence, evidenceOn);
            if (allowApproval)
                world.Segment = EvidenceBundles.Allowed;

            var session = await world.ReadyStayAsync(keepsNoDocument);

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
