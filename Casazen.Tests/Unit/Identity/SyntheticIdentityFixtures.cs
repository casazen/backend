using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

// A picture is known by what it shows (the SHA-256 of its decoded pixels, and of the bytes of its JPEG for the liveness): the application decodes the frames
// of a request again, so a double keyed by the object cannot serve a request over HTTP.
internal sealed class ScriptedModels : IFaceDetector, IFaceEmbedder, IAntiSpoofingService
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<DetectedFace>> _faces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _liveness = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _embedded = new();
    private string? _documentKey;
    private IReadOnlyList<DetectedFace> _documentFaces = [];
    private int _detections;
    private int _embeddings;
    private int _livenessCalls;
    private int _portraitEmbeddings;

    public double Liveness { get; set; } = 0.99;

    // The cosine of the first person of the script with the portrait of the document.
    public double Similarity { get; set; } = 0.99;

    public Exception? DetectorFailure { get; set; }

    public bool DetectorStalls { get; set; }

    public Exception? EmbedderFailure { get; set; }

    public Exception? LivenessFailure { get; set; }

    // Answers for the pictures the script does not know: the host shows the models the generated card at the document step, before the script can know it.
    public IFaceDetector? Fallback { get; set; }

    public int Detections => Volatile.Read(ref _detections);

    public int Embeddings => Volatile.Read(ref _embeddings);

    public int LivenessCalls => Volatile.Read(ref _livenessCalls);

    public int PortraitEmbeddings => Volatile.Read(ref _portraitEmbeddings);

    public bool PortraitWasEmbedded => _documentKey is not null && _embedded.Contains(_documentKey);

    public static string Key(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static float[] PortraitFor(double similarity)
    {
        var vector = new float[8];
        vector[0] = (float)similarity;
        vector[1] = (float)Math.Sqrt(Math.Max(0, 1 - (similarity * similarity)));
        return vector;
    }

    // The detector is shown the frame itself, not a reduced copy, so a frame must fit the side it is shown.
    public PreparedSequence Prepare(ScriptedSequence sequence, IdentityDecisionPolicy policy, double? liveness = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(policy);
        if (Math.Max(sequence.Width, sequence.Height) > policy.Presence.Sequence.DetectorMaxSide)
            throw new InvalidOperationException("The scripted frames must not be larger than the side the detector is shown.");

        var maxPixels = new BiometricModelOptions().MaxImagePixels;
        var jpegs = new List<byte[]>(sequence.Frames.Count);
        foreach (var frame in sequence.Frames)
        {
            var jpeg = TestJpeg.Gray(sequence.Width, sequence.Height, frame.Gray);
            using (var decoded = RasterImage.Decode(jpeg, maxPixels))
            {
                var pictureKey = Key(decoded.Rgb);
                _faces[pictureKey] = [.. frame.Faces];
                _vectors[pictureKey] = [.. frame.Person];
            }

            _liveness[Key(jpeg)] = liveness ?? Liveness;
            jpegs.Add(jpeg);
        }

        return new PreparedSequence(jpegs, PreparedSequence.TimestampsOf(sequence.Frames.Select(frame => frame.TimestampMs)), sequence.ReceivedAfterIssue);
    }

    public void UseDocument(byte[] picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        using var decoded = RasterImage.Decode(picture, new BiometricModelOptions().MaxImagePixels);
        _documentKey = Key(decoded.Rgb);
        _documentFaces = [SequenceTestFrames.Face(decoded.Width, decoded.Height)];
    }

    public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _detections);
        if (DetectorStalls)
        {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (DetectorFailure is not null)
            throw DetectorFailure;

        var key = Key(image.Rgb);
        if (string.Equals(key, _documentKey, StringComparison.Ordinal))
            return _documentFaces;

        if (_faces.TryGetValue(key, out var faces))
            return faces;

        return Fallback?.Detect(image, minScore, cancellationToken) ?? [];
    }

    public float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _embeddings);
        cancellationToken.ThrowIfCancellationRequested();
        if (EmbedderFailure is not null)
            throw EmbedderFailure;

        var key = Key(image.Rgb);
        _embedded.Enqueue(key);
        if (string.Equals(key, _documentKey, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _portraitEmbeddings);
            return PortraitFor(Similarity);
        }

        // A fresh copy each time: the application zeroes what it is given.
        if (_vectors.TryGetValue(key, out var vector))
            return (float[])vector.Clone();

        // With a fallback the picture is one the fallback found a portrait in: a card, whose portrait is the one of the document.
        if (Fallback is not null)
        {
            Interlocked.Increment(ref _portraitEmbeddings);
            return PortraitFor(Similarity);
        }

        throw new InvalidOperationException("The script of the models knows no such picture.");
    }

    public async Task<bool> DetectLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
        (await ScoreLivenessAsync(imageStream, cancellationToken)).IsLive;

    public async Task<LivenessResult> ScoreLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _livenessCalls);
        using var copy = new MemoryStream();
        await imageStream.CopyToAsync(copy, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (LivenessFailure is not null)
            throw LivenessFailure;

        var score = _liveness.TryGetValue(Key(copy.ToArray()), out var scripted) ? scripted : Liveness;
        return new LivenessResult(score >= new BiometricModelOptions().LivenessThreshold, score);
    }
}

internal sealed class PreparedSequence(IReadOnlyList<byte[]> jpegs, string timestampsJson, TimeSpan receivedAfterIssue)
{
    public IReadOnlyList<byte[]> Jpegs { get; } = jpegs;

    public string TimestampsJson { get; } = timestampsJson;

    public TimeSpan ReceivedAfterIssue { get; } = receivedAfterIssue;

    public int Count => Jpegs.Count;

    public List<PresenceFrameUpload> Uploads() =>
        [.. Jpegs.Select(bytes => new PresenceFrameUpload(new MemoryStream(bytes, writable: false), bytes.Length, "image/jpeg"))];

    public static string TimestampsOf(IEnumerable<long> timestamps) =>
        "[" + string.Join(',', timestamps.Select(timestamp => timestamp.ToString(CultureInfo.InvariantCulture))) + "]";
}

internal static class SyntheticIdentityFixtures
{
    public static readonly CancellationToken Ct = CancellationToken.None;

    public static string NewKey() => Guid.NewGuid().ToString();

    public static ScriptedSequence StillFrameRepeated(IdentityDecisionPolicy policy) => SequenceTestFrames.StaticReplay(policy);

    // Two pixels change by one grey level from frame to frame, far under the continuity rule (MeanDifferenceFromTheFirst proves it),
    // and the face moves by a few pixels and a degree or two.
    public static ScriptedSequence StillFrameWithJitter(IdentityDecisionPolicy policy, int seed = 11)
    {
        var sequence = SequenceTestFrames.StaticReplay(policy);
        var random = new Random(seed);
        foreach (var frame in sequence.Frames.Skip(1))
        {
            for (var change = 0; change < 2; change++)
            {
                var pixel = random.Next(frame.Gray.Length);
                frame.Gray[pixel] = (byte)Math.Clamp(frame.Gray[pixel] + (random.Next(2) == 0 ? -1 : 1), 0, 255);
            }

            frame.Faces =
            [
                SequenceTestFrames.Face(
                    sequence.Width,
                    sequence.Height,
                    yawDegrees: Jitter(random, 1.5),
                    pitchDegrees: Jitter(random, 1.0),
                    centerX: 0.5 + Jitter(random, 0.02),
                    centerY: 0.5 + Jitter(random, 0.02),
                    widthShare: 0.5 * (1 + Jitter(random, 0.03))),
            ];
        }

        return sequence;
    }

    public static ScriptedSequence NeutralAndTurnedPair(IdentityDecisionPolicy policy)
    {
        var genuine = SequenceTestFrames.Genuine(policy);
        var sequence = new ScriptedSequence { Challenge = genuine.Challenge, Width = genuine.Width, Height = genuine.Height };
        sequence.Frames.Add(new ScriptedFrame
        {
            TimestampMs = 0,
            Gray = TestJpeg.NoisePixels(sequence.Width, sequence.Height, 2001),
            Faces = [SequenceTestFrames.Face(sequence.Width, sequence.Height)],
            Person = SequenceTestFrames.Person(0),
        });
        sequence.Frames.Add(new ScriptedFrame
        {
            TimestampMs = 1500,
            Gray = TestJpeg.NoisePixels(sequence.Width, sequence.Height, 2002),
            Faces = [SequenceTestFrames.Face(sequence.Width, sequence.Height, yawDegrees: 25)],
            Person = SequenceTestFrames.Person(0),
        });
        sequence.ReceivedAfterIssue = TimeSpan.FromMilliseconds(sequence.DurationMs + 1500);
        return sequence;
    }

    public static double MeanDifferenceFromTheFirst(PreparedSequence sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        var maxPixels = new BiometricModelOptions().MaxImagePixels;
        using var first = RasterImage.Decode(sent.Jpegs[0], maxPixels);
        var farthest = 0.0;
        foreach (var jpeg in sent.Jpegs.Skip(1))
        {
            using var other = RasterImage.Decode(jpeg, maxPixels);
            long sum = 0;
            for (var index = 0; index < first.Rgb.Length; index++)
                sum += Math.Abs(first.Rgb[index] - other.Rgb[index]);

            farthest = Math.Max(farthest, (double)sum / first.Rgb.Length);
        }

        return farthest;
    }

    // Generated noise, never the picture of a document: the repository is public.
    public static byte[] DocumentPicture(int seed = 7) => TestJpeg.Noise(120, 80, seed);

    private static double Jitter(Random random, double amplitude) => ((random.NextDouble() * 2) - 1) * amplitude;
}

// The quality of the pictures of the document is the business of the geometry, not of these proofs.
internal sealed class AcceptingAgent : IIdentityVerificationAgent
{
    public Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct) =>
        Task.FromResult(new CaptureAssessment(true, [], [], true));

    public Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct) =>
        throw new NotSupportedException("The self-hosted flow runs no verification of the legacy agent.");
}

// The real pipeline of the application over scripted models: a decision is the policy's, never scripted.
internal sealed class SyntheticIdentityWorld : IAsyncDisposable
{
    private const string PolicyFile = "identity-policy-of-the-synthetic-world.json";
    private const string ServedConsentVersion = "TEST-biometric-v1";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-synthetic-identity-" + Guid.NewGuid().ToString("N"));
    private readonly IdentityPolicyProvider _policyProvider;
    private readonly DocumentFormatRegistry _formats = PresenceClosureKit.Formats();

    private SyntheticIdentityWorld(PresenceClosureWorld inner, string policyText, Func<SyntheticIdentityWorld, IPresenceEvidenceCollector>? collector)
    {
        Inner = inner;
        Directory.CreateDirectory(_directory);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(policyText);
        File.WriteAllBytes(Path.Combine(_directory, PolicyFile), bytes);
        var pin = IdentityPolicyProvider.CanonicalSha256(bytes) ?? throw new InvalidOperationException("The policy text of the world is not strict JSON.");
        _policyProvider = new IdentityPolicyProvider(
            Options.Create(new IdentityVerificationOptions { PolicyFile = PolicyFile, PolicySha256 = pin }),
            NullLogger<IdentityPolicyProvider>.Instance,
            _directory);
        Policy = JsonSerializer.Deserialize<IdentityDecisionPolicy>(policyText, Web) ?? throw new InvalidOperationException("The policy text of the world is not a policy.");
        Assert.True(_policyProvider.Snapshot.IsReady, "The policy of the synthetic world must be usable.");

        DocumentPicture = SyntheticIdentityFixtures.DocumentPicture();
        Models.UseDocument(DocumentPicture);
        Collector = collector?.Invoke(this) ?? RealPipeline();
    }

    public PresenceClosureWorld Inner { get; }

    public ScriptedModels Models { get; } = new();

    public IdentityDecisionPolicy Policy { get; }

    public IdentityVerificationOptions Limits { get; } = new();

    public IPresenceEvidenceCollector Collector { get; }

    public byte[] DocumentPicture { get; }

    public bool ModelsReady { get; set; } = true;

    public PortraitStandInDetector DocumentDetector { get; } = new();

    public DocumentStepSelfHostedTests.ScriptedMrz DocumentMrz { get; } = new();

    public DocumentStepSelfHostedTests.ScriptedZonedReader DocumentZones { get; } = new();

    public static async Task<SyntheticIdentityWorld> CreateAsync(string? policyText = null, Func<SyntheticIdentityWorld, IPresenceEvidenceCollector>? collector = null)
    {
        var inner = await PresenceClosureWorld.CreateAsync();
        return new SyntheticIdentityWorld(inner, policyText ?? IdentityPolicyTestFiles.ShippedText(), collector);
    }

    public IdentityVerificationFlowService Flow(ILogger<IdentityVerificationFlowService>? logger = null)
    {
        var context = Inner.Db;
        return new IdentityVerificationFlowService(
            context,
            Mock.Of<IIdentityVerificationAgent>(),
            Inner.Alerts,
            Inner.AccessCodes.Object,
            Mock.Of<IEmailQueue>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            new StayGuestService(context, Mock.Of<IAlloggiatiCodeTableService>()),
            Inner.Storage,
            Inner.DataProtection,
            Inner.Clock,
            logger ?? NullLogger<IdentityVerificationFlowService>.Instance,
            domainEvents: Inner.Events.Object,
            legalTexts: Inner.Registry.Object,
            featureFlags: Inner.Flags,
            identityPolicy: _policyProvider,
            biometricModels: new FakeBiometricModelStore(ModelsReady),
            identityOptions: Options.Create(Limits),
            presenceIssuer: new PresenceChallengeIssuer(),
            formats: _formats,
            frameReader: new FrameSequenceReader(Options.Create(Limits), Options.Create(new BiometricModelOptions()), NullLogger<FrameSequenceReader>.Instance),
            evidenceCollector: Collector,
            segments: Inner.Segments.Object,
            decisionEngine: new IdentityDecisionEngine());
    }

    public IdentityVerificationFlowService DocumentFlow()
    {
        var context = Inner.Db;
        var biometric = Options.Create(new BiometricModelOptions());
        var inspector = new DocumentGeometryInspector(DocumentDetector, biometric);
        return new IdentityVerificationFlowService(
            context,
            new AcceptingAgent(),
            Inner.Alerts,
            Inner.AccessCodes.Object,
            Mock.Of<IEmailQueue>(),
            DocumentMrz,
            new DocumentStepSelfHostedTests.EvidentDocumentOcr(),
            new StayGuestService(context, Mock.Of<IAlloggiatiCodeTableService>()),
            Inner.Storage,
            Inner.DataProtection,
            Inner.Clock,
            NullLogger<IdentityVerificationFlowService>.Instance,
            legalTexts: Inner.Registry.Object,
            activePose: new ActivePoseFlowTestVerifier(),
            featureFlags: Inner.Flags,
            documentPictures: new DocumentPictureAnalyzer(inspector, DocumentZones, biometric),
            sideClassifier: new DocumentSideClassifier(),
            coherenceChecker: new DocumentCoherenceChecker(),
            formats: _formats,
            identityPolicy: _policyProvider);
    }

    // The doubles know DocumentPicture, so the kept front is replaced by it.
    public async Task<GuestCheckInSession> ReadyStayAsync()
    {
        var before = Inner.Storage.Objects.Keys.ToHashSet(StringComparer.Ordinal);
        var session = await Inner.ReadyStayAsync();
        var kept = Inner.Storage.Objects.Keys.Except(before, StringComparer.Ordinal).Single();
        Inner.Storage.Objects[kept] = Inner.DataProtection.CreateProtector(IdentityVerificationFlowService.DocumentImageContentPurpose).Protect(DocumentPicture);
        return session;
    }

    public async Task<GuestCheckInSession> ConsentedStayAsync(Action<Guest>? describe = null)
    {
        var now = PresenceClosureKit.Now.UtcDateTime;
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = Inner.OrgId,
            FirstName = DocumentStepSelfHostedTests.GivenNames,
            LastName = DocumentStepSelfHostedTests.Surname,
            Email = "zefirino@example.com",
            DateOfBirth = new DateTime(1987, 3, 19),
        };
        describe?.Invoke(guest);
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = Inner.OrgId,
            PropertyId = Inner.PropertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 28),
            CheckOutDate = new DateTime(2026, 11, 30),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };
        var session = new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = Inner.OrgId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = now.AddDays(7),
            Status = GuestCheckInSessionStatus.InCompilazione,
            Language = "it",
            CreatedAt = now,
            UpdatedAt = now,
        };
        Inner.Db.Guests.Add(guest);
        Inner.Db.Bookings.Add(booking);
        Inner.Db.GuestCheckInSessions.Add(session);
        await Inner.Db.SaveChangesAsync();

        var loaded = await Inner.LoadAsync(session.Id);
        var consent = await DocumentFlow().RecordConsentAsync(loaded, true, ServedConsentVersion, IdentityChannels.AndroidChrome, SyntheticIdentityFixtures.Ct);
        Assert.Equal(BiometricConsentStatus.Recorded, consent.Status);
        return loaded;
    }

    public async Task<PresenceChallengeIssue> IssueAsync(GuestCheckInSession session)
    {
        var issue = await Flow().IssuePresenceChallengeAsync(session, SyntheticIdentityFixtures.Ct);
        Assert.NotNull(issue);
        return issue;
    }

    public async Task<(PresenceStepResult Answer, PreparedSequence Sent, PresenceChallengeIssue Issue)> AttemptAsync(
        GuestCheckInSession session,
        Func<IReadOnlyList<string>, ScriptedSequence> script,
        double? liveness = null,
        double? similarity = null,
        string? key = null)
    {
        var issue = await IssueAsync(session);
        if (similarity is { } declared)
            Models.Similarity = declared;

        var sent = Models.Prepare(script(issue.Steps), Policy, liveness);
        var answer = await SubmitAsync(session, sent, issue, key);
        return (answer, sent, issue);
    }

    public async Task<PresenceStepResult> SubmitAsync(GuestCheckInSession session, PreparedSequence sent, PresenceChallengeIssue issue, string? key = null)
    {
        Inner.Clock.Advance(sent.ReceivedAfterIssue);
        return await Flow().SubmitPresenceSequenceAsync(
            session,
            new PresenceSubmission(key ?? SyntheticIdentityFixtures.NewKey(), issue.ChallengeToken, sent.TimestampsJson, sent.Uploads()),
            SyntheticIdentityFixtures.Ct);
    }

    public Task<List<BiometricVerificationLog>> LogRowsAsync(GuestCheckInSession session) => Inner.LogRowsAsync(session);

    public Task<GuestCheckInSession> ReadBackAsync(Guid sessionId) => Inner.ReadBackAsync(sessionId);

    public async Task AssertNeverApprovedAsync(GuestCheckInSession session, PresenceStepResult? answer, bool shadowApprovalAllowed = false)
    {
        if (answer is not null)
        {
            Assert.NotEqual(IdentityVerificationGuestResults.Ok, answer.Result);
            Assert.Null(answer.LockCode);
        }

        Inner.AccessCodes.Verify(codes => codes.ReleaseToGuestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Inner.Events.Verify(events => events.Record(DomainEventType.IdVerificationCompleted, It.IsAny<Guid>(), It.IsAny<object>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Never);
        var saved = await ReadBackAsync(session.Id);
        Assert.NotEqual(BiometricOutcome.Accept, saved.BiometricOutcome);
        Assert.NotEqual(IdentityOutcomeCodes.AutoApproved, saved.IdentityOutcomeCode);
        foreach (var row in await LogRowsAsync(session))
        {
            Assert.NotEqual(BiometricOutcome.Accept, row.Outcome);
            if (!shadowApprovalAllowed)
                Assert.NotEqual(BiometricOutcome.Accept, row.ShadowOutcome);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Inner.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private PresenceEvidenceCollector RealPipeline()
    {
        var biometric = Options.Create(new BiometricModelOptions());
        var identity = Options.Create(Limits);
        return new PresenceEvidenceCollector(
            new PresenceSequenceValidator(Models, Models, biometric, identity, NullLogger<PresenceSequenceValidator>.Instance),
            new PresenceFrameSelector(),
            new SequenceLivenessEvaluator(Models, biometric, NullLogger<SequenceLivenessEvaluator>.Instance),
            new PresenceMatcher(Models, Models, biometric, NullLogger<PresenceMatcher>.Instance),
            biometric);
    }
}
