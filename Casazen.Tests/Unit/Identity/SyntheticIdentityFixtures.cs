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

// E74/F02/US01/T01 (NF-198, NF-192, decisions D6, D7, D12): the shared builder of the synthetic proofs of the self-hosted identity verification. Everything here is generated in code: no
// photograph of a person, no picture of a document, no value read on a document is in the repository (it is public), and nothing here prints or logs an image, a frame, a name, a
// document number or an embedding. The acceptance scenario of E74/F02/US01/T02 (the HTTP host) takes the models, the sequences and the pictures from here.

/// <summary>
/// The doubles of the three face models, written so that the REAL validation, the REAL choice of the frames, the REAL liveness and the REAL match of the application run over them, and so
/// that they work wherever the frames come from: the frames of a request are decoded again by the application (a new picture each time), so a double that knows a frame by the object (like
/// <see cref="FrameKeyedEmbedder"/>) cannot serve a request over HTTP. These know a picture by what it shows (the SHA-256 of its decoded pixels, and of the bytes of its JPEG for the liveness),
/// so the same script answers in the flow, in the closure and in the host. Thread safe (two requests at once); what is scripted is the answer of a model, never a verdict of the policy.
/// </summary>
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

    /// <summary>The score the anti-spoofing double gives a frame the script gave none: a screen replay is scripted with a low one, a person with a high one.</summary>
    public double Liveness { get; set; } = 0.99;

    /// <summary>The cosine of a person of the script (<see cref="SequenceTestFrames.Person"/> 0) with the portrait of the document: what the match finds.</summary>
    public double Similarity { get; set; } = 0.99;

    /// <summary>The detector throws this on every call.</summary>
    public Exception? DetectorFailure { get; set; }

    /// <summary>The detector waits (up to 20 seconds) for the token to be cancelled, then raises the cancellation: a model that never answers.</summary>
    public bool DetectorStalls { get; set; }

    /// <summary>The embedder throws this on every call.</summary>
    public Exception? EmbedderFailure { get; set; }

    /// <summary>The anti-spoofing double throws this on every call.</summary>
    public Exception? LivenessFailure { get; set; }

    /// <summary>
    /// What answers for the pictures the script does not know. The host of the acceptance scenario shows the models the pictures of the card at the document step, before the script can
    /// know them: a detector that finds the stand-in of the portrait the generated cards draw answers there, and the portrait it finds is the one of the document (the embedder gives it the
    /// vector that makes <see cref="Similarity"/>). Null: a picture the script does not know has no face, and no vector.
    /// </summary>
    public IFaceDetector? Fallback { get; set; }

    /// <summary>How many pictures the detector was shown.</summary>
    public int Detections => Volatile.Read(ref _detections);

    /// <summary>How many faces the embedder was asked for.</summary>
    public int Embeddings => Volatile.Read(ref _embeddings);

    /// <summary>How many frames the anti-spoofing double was asked about.</summary>
    public int LivenessCalls => Volatile.Read(ref _livenessCalls);

    /// <summary>How many times the embedder was asked for the portrait of a document (the match ran that many times).</summary>
    public int PortraitEmbeddings => Volatile.Read(ref _portraitEmbeddings);

    /// <summary>Whether the embedder was ever asked for the face of the portrait of the document (the match ran).</summary>
    public bool PortraitWasEmbedded => _documentKey is not null && _embedded.Contains(_documentKey);

    /// <summary>The SHA-256 (upper case hexadecimal) of <paramref name="bytes"/>: how a picture is known.</summary>
    public static string Key(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>The vector of the portrait of the document for a similarity of <paramref name="similarity"/> with a person of the script (the first axis).</summary>
    public static float[] PortraitFor(double similarity)
    {
        var vector = new float[8];
        vector[0] = (float)similarity;
        vector[1] = (float)Math.Sqrt(Math.Max(0, 1 - (similarity * similarity)));
        return vector;
    }

    /// <summary>
    /// Writes the frames of <paramref name="sequence"/> as the JPEG pictures the browser would send and teaches the doubles what each one shows: its faces and the vector of its person
    /// (by the pixels the application will decode from the very bytes), and the score of its liveness (by the bytes). The frames must fit the side the detector is shown, so the
    /// application shows the detector the frame itself and not a reduced copy.
    /// </summary>
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

    /// <summary>Teaches the doubles the portrait of a document: the picture (a JPEG or a PNG) has one face, and the embedder gives it the vector that makes <see cref="Similarity"/>.</summary>
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

/// <summary>
/// A sequence written as the browser sends it: the JPEG of each frame, the timestamps as the JSON array of the request, and how long after the challenge the server received it. The bytes are
/// the caller's to keep: every <see cref="Uploads"/> gives new streams over them.
/// </summary>
internal sealed class PreparedSequence(IReadOnlyList<byte[]> jpegs, string timestampsJson, TimeSpan receivedAfterIssue)
{
    public IReadOnlyList<byte[]> Jpegs { get; } = jpegs;

    /// <summary>The <c>timestamps</c> part of the request: a JSON array of integers in milliseconds.</summary>
    public string TimestampsJson { get; } = timestampsJson;

    /// <summary>How long after the challenge was issued the sequence reaches the server: the test moves its clock by this much before it sends the request.</summary>
    public TimeSpan ReceivedAfterIssue { get; } = receivedAfterIssue;

    public int Count => Jpegs.Count;

    /// <summary>The frames as the parts of a request, over new read-only streams.</summary>
    public List<PresenceFrameUpload> Uploads() =>
        [.. Jpegs.Select(bytes => new PresenceFrameUpload(new MemoryStream(bytes, writable: false), bytes.Length, "image/jpeg"))];

    /// <summary>A JSON array of integers.</summary>
    public static string TimestampsOf(IEnumerable<long> timestamps) =>
        "[" + string.Join(',', timestamps.Select(timestamp => timestamp.ToString(CultureInfo.InvariantCulture))) + "]";
}

/// <summary>The sequences of the synthetic proofs, built on <see cref="SequenceTestFrames"/>: what the previous proof accepted, and what must never be accepted.</summary>
internal static class SyntheticIdentityFixtures
{
    public static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>The key of a new attempt (a UUID fits the header).</summary>
    public static string NewKey() => Guid.NewGuid().ToString();

    /// <summary>A still picture played again: the same frame, with its face where it was, every time.</summary>
    public static ScriptedSequence StillFrameRepeated(IdentityDecisionPolicy policy) => SequenceTestFrames.StaticReplay(policy);

    /// <summary>
    /// A still picture held a little differently each time (a hand that trembles, a screen that is moved): the sensor of a camera that films it changes two pixels by a grey level from one
    /// frame to the next (the frames are not the same bytes, and the mean difference of their pixels from the first one stays a small share of a grey level, far under the least the policy
    /// asks of a recording that moves: <see cref="MeanDifferenceFromTheFirst"/> proves it), and the face moves by a few pixels, a few percent of its size and a degree or two. Far from what
    /// a head that answers the challenge does.
    /// </summary>
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

    /// <summary>
    /// The two stills of the previous proof sent as a sequence of two frames: a neutral one and one turned by 25 degrees, a second and a half apart. Both are the same person with a face the
    /// detector finds, and the models are scripted to say what they said then (live, a perfect match).
    /// </summary>
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

    /// <summary>
    /// The largest mean difference, in grey levels, between a frame of <paramref name="sent"/> and its first frame, as the application sees them (the JPEG decoded): the number the rule of the
    /// still picture reads (<c>presence.continuity.minPixelDifference</c>). A sequence under the least of the policy is a still picture played again.
    /// </summary>
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

    /// <summary>A picture of a document that the models of the doubles can decode (a JPEG of generated noise, never the picture of a document).</summary>
    public static byte[] DocumentPicture(int seed = 7) => TestJpeg.Noise(120, 80, seed);

    private static double Jitter(Random random, double amplitude) => ((random.NextDouble() * 2) - 1) * amplitude;
}

/// <summary>An agent that finds every capture good: the quality of the pictures of the document is the business of the geometry, not of these proofs.</summary>
internal sealed class AcceptingAgent : IIdentityVerificationAgent
{
    public Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct) =>
        Task.FromResult(new CaptureAssessment(true, [], [], true));

    public Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct) =>
        throw new NotSupportedException("The self-hosted flow runs no verification of the legacy agent.");
}

/// <summary>
/// The world of the synthetic proofs: the world of the closure of the presence (<see cref="PresenceClosureWorld"/>: the database in memory, the clock, the storage, the alerts, the
/// segments) with, in place of the scripted pipeline, the REAL pipeline of the application (validation of the sequence, choice of the frames, liveness, match, the engine of the policy)
/// over <see cref="ScriptedModels"/>. A decision is therefore the policy's, taken on what the models say, never scripted. The policy is the committed file (or an edit of it), pinned by its
/// canonical hash; the limits of the request are <see cref="Limits"/>.
/// </summary>
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

    /// <summary>The world of the closure that this one builds on: its database, clock, storage, alerts and segments.</summary>
    public PresenceClosureWorld Inner { get; }

    /// <summary>The doubles of the models the pipeline runs over.</summary>
    public ScriptedModels Models { get; } = new();

    /// <summary>The policy in force, as a value (the sequences of the proofs are written from it).</summary>
    public IdentityDecisionPolicy Policy { get; }

    /// <summary>The limits of the request and of the sequence (<c>IdentityVerification</c>): the flow, the reader of the frames and the validation read this very object.</summary>
    public IdentityVerificationOptions Limits { get; } = new();

    /// <summary>The pipeline of the presence the closure runs.</summary>
    public IPresenceEvidenceCollector Collector { get; }

    /// <summary>The picture of the front of the document the stays of this world keep.</summary>
    public byte[] DocumentPicture { get; }

    /// <summary>False makes the store of the models report that they are not loaded.</summary>
    public bool ModelsReady { get; set; } = true;

    /// <summary>The detector of the pictures of a card, for the document step (a stand-in that finds the portrait the factory draws).</summary>
    public PortraitStandInDetector DocumentDetector { get; } = new();

    /// <summary>What the reader of the MRZ says of the pictures it was told about (the document step).</summary>
    public DocumentStepSelfHostedTests.ScriptedMrz DocumentMrz { get; } = new();

    /// <summary>What the reader of the zones says of each side (the document step).</summary>
    public DocumentStepSelfHostedTests.ScriptedZonedReader DocumentZones { get; } = new();

    /// <summary>The world with the committed policy (or its text with the edits of <paramref name="policyText"/>), a segment in observation unless the test says otherwise.</summary>
    public static async Task<SyntheticIdentityWorld> CreateAsync(string? policyText = null, Func<SyntheticIdentityWorld, IPresenceEvidenceCollector>? collector = null)
    {
        var inner = await PresenceClosureWorld.CreateAsync();
        return new SyntheticIdentityWorld(inner, policyText ?? IdentityPolicyTestFiles.ShippedText(), collector);
    }

    /// <summary>The flow of the application with the closure over the pipeline of this world.</summary>
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

    /// <summary>The flow with the services of the document step (the geometry, the side, the coherence), over the doubles of the readers of the document.</summary>
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

    /// <summary>A stay whose guest opened the identity step, gave the consent, and has the Italian card read and checked (coherent), its front kept encrypted: the front is a picture the doubles decode.</summary>
    public async Task<GuestCheckInSession> ReadyStayAsync()
    {
        var before = Inner.Storage.Objects.Keys.ToHashSet(StringComparer.Ordinal);
        var session = await Inner.ReadyStayAsync();
        var kept = Inner.Storage.Objects.Keys.Except(before, StringComparer.Ordinal).Single();
        Inner.Storage.Objects[kept] = Inner.DataProtection.CreateProtector(IdentityVerificationFlowService.DocumentImageContentPurpose).Protect(DocumentPicture);
        return session;
    }

    /// <summary>A stay whose guest gave the biometric consent and opened the identity step, with no document yet (the guest the cards of the document step name).</summary>
    public async Task<GuestCheckInSession> ConsentedStayAsync()
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

    /// <summary>Asks for the challenge of the next attempt of <paramref name="session"/> (the real one: the moves are drawn at random from the policy).</summary>
    public async Task<PresenceChallengeIssue> IssueAsync(GuestCheckInSession session)
    {
        var issue = await Flow().IssuePresenceChallengeAsync(session, SyntheticIdentityFixtures.Ct);
        Assert.NotNull(issue);
        return issue;
    }

    /// <summary>
    /// One attempt as the page makes it: the challenge, the sequence <paramref name="script"/> writes for its moves (the models taught what it shows, with the liveness
    /// <paramref name="liveness"/> and the match <paramref name="similarity"/> when given), the clock moved to when the sequence reaches the server, and the request with the key
    /// <paramref name="key"/> (a new one by default). Returns the answer and what was sent.
    /// </summary>
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

    /// <summary>Moves the clock to when <paramref name="sent"/> reaches the server and sends it with the proof of <paramref name="issue"/>.</summary>
    public async Task<PresenceStepResult> SubmitAsync(GuestCheckInSession session, PreparedSequence sent, PresenceChallengeIssue issue, string? key = null)
    {
        Inner.Clock.Advance(sent.ReceivedAfterIssue);
        return await Flow().SubmitPresenceSequenceAsync(
            session,
            new PresenceSubmission(key ?? SyntheticIdentityFixtures.NewKey(), issue.ChallengeToken, sent.TimestampsJson, sent.Uploads()),
            SyntheticIdentityFixtures.Ct);
    }

    /// <summary>The log rows of the booking of <paramref name="session"/>, read by another context.</summary>
    public Task<List<BiometricVerificationLog>> LogRowsAsync(GuestCheckInSession session) => Inner.LogRowsAsync(session);

    /// <summary>The session as it was saved, read by another context.</summary>
    public Task<GuestCheckInSession> ReadBackAsync(Guid sessionId) => Inner.ReadBackAsync(sessionId);

    /// <summary>
    /// What must hold of every attempt of these proofs: nothing was approved. The answer is not <c>ok</c> and carries no lock code, no code was released, no approval was announced on the
    /// bus, and no row of the log is an approval or a shadow approval (<paramref name="shadowApprovalAllowed"/> allows the shadow of an observation).
    /// </summary>
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

    /// <summary>The pipeline of the application over the doubles of the models: the very classes the registrations of the application build.</summary>
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
