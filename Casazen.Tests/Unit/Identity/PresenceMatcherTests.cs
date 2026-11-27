using System.Reflection;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E72/F01/US02/T02 (NF-193, ADR-013-R17, decision D7): the portrait of the document is compared with the frames the server chose, with the aggregation rule and the thresholds of
/// the frozen policy. The aggregate follows <c>match.aggregation</c> (median, min, mean), an acceptance needs the aggregate AND every frame above their thresholds, the review band
/// is reported apart, a document with no face is its own state, and a model that is not ready, fails or runs out of time is never a match. The comparison takes no presence input at
/// all (no compensation is possible by construction); the vectors are zeroed after use. The faces and the vectors come from doubles that answer picture by picture (no photograph:
/// the repository is public); a frame's vector is written so that its cosine with the portrait is the similarity the test wants.
/// </summary>
public class PresenceMatcherTests
{
    private static readonly IdentityDecisionPolicy Policy = IdentityTestPolicy.Shipped();

    // ─── The proof of the brief ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (security, NF-193, decision D7): there is no compensation between a low presence score and a high similarity, by construction. <see cref="PresenceMatcher.MatchAsync"/>
    /// takes the document, the frames the server chose, the policy and a token, and nothing of the presence: no liveness report, no validation report, no challenge, no continuity;
    /// the class holds no service that could ask for them; and the report has no member of the presence. The AND of the independent thresholds is the decision engine's (E73), where the
    /// behavioural proof is.
    /// </summary>
    [Fact]
    public void PresenceMatcher_HasNoPresenceInput()
    {
        var method = typeof(PresenceMatcher).GetMethod(nameof(PresenceMatcher.MatchAsync))!;
        Assert.Equal(
            [typeof(RasterImage), typeof(FrameSequence), typeof(IReadOnlyList<int>), typeof(IdentityDecisionPolicy), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));

        Type[] presence =
        [
            typeof(LivenessReport), typeof(FrameLiveness), typeof(SequenceValidationReport), typeof(SequenceFailure), typeof(FrameObservation), typeof(StepPeak),
            typeof(StoredPresenceChallenge), typeof(SelectedFrames), typeof(FrameSelection), typeof(PresenceSequenceValidator), typeof(SequenceLivenessEvaluator),
            typeof(PresenceFrameSelector),
        ];
        var constructor = Assert.Single(typeof(PresenceMatcher).GetConstructors());
        Assert.Equal(
            [typeof(IFaceDetector), typeof(IFaceEmbedder), typeof(IOptions<BiometricModelOptions>), typeof(ILogger<PresenceMatcher>)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        var heldTypes = method.GetParameters().Select(parameter => parameter.ParameterType)
            .Concat(constructor.GetParameters().Select(parameter => parameter.ParameterType))
            .Concat(typeof(PresenceMatcher).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static).Select(field => field.FieldType))
            .ToList();
        Assert.DoesNotContain(heldTypes, type => presence.Any(other => other.IsAssignableFrom(type) || type.IsAssignableFrom(other)));

        // What it says: the state and four numbers of the comparison, nothing of the presence.
        Assert.Equal(
            ["AcceptPassed", "Aggregate", "FrameCount", "InReviewBand", "Min", "State"],
            typeof(MatchReport).GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.DeclaringType == typeof(MatchReport))
                .Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    // ─── Acceptance criterion 1: the aggregate and the acceptance ───────────────────────────────────────

    [Theory]
    [InlineData("median", 0.90)]
    [InlineData("min", 0.80)]
    [InlineData("mean", 0.883333333)]
    public async Task MatchAsync_TheAggregateFollowsTheRuleOfThePolicy(string rule, double expected)
    {
        using var scene = Build([0.9, 0.8, 0.95]);
        var policy = Policy with { Match = Policy.Match with { Aggregation = rule } };

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, policy, CancellationToken.None);

        Assert.Equal(MatchState.Ok, report.State);
        Assert.Equal(expected, report.Aggregate, 5);
        Assert.Equal(0.8, report.Min, 5);
        Assert.Equal(3, report.FrameCount);
    }

    [Fact]
    public async Task MatchAsync_TheMedianOfAnEvenNumberOfFrames_IsTheMeanOfTheTwoInTheMiddle()
    {
        using var scene = Build([0.9, 0.7, 0.8, 1.0]);
        var policy = Policy with { Match = Policy.Match with { Frames = 4 } };

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, [0, 1, 2, 3], policy, CancellationToken.None);

        Assert.Equal(0.85, report.Aggregate, 5);
        Assert.Equal(0.7, report.Min, 5);
        Assert.Equal(4, report.FrameCount);
    }

    [Fact]
    public async Task MatchAsync_EveryFrameAndTheAggregateAboveTheirThresholds_IsAcceptPassed_AndNotInTheBand()
    {
        using var scene = Build([0.92, 0.95, 0.9]);

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.Equal(MatchState.Ok, report.State);
        Assert.True(report.AcceptPassed);
        Assert.False(report.InReviewBand);
    }

    [Fact]
    public async Task MatchAsync_OneWeakFrameWithAHighMedian_FailsMinEachFrame()
    {
        // The median (0.95) reaches the accept threshold of the policy (0.85), the weakest frame (0.6) does not reach minEachFrame (0.7): no acceptance, and the aggregate is not
        // under the accept threshold either, so it is not in the review band: the engine decides what a person is asked.
        using var scene = Build([0.95, 0.95, 0.6]);

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.Equal(MatchState.Ok, report.State);
        Assert.Equal(0.95, report.Aggregate, 5);
        Assert.Equal(0.6, report.Min, 5);
        Assert.False(report.AcceptPassed);
        Assert.False(report.InReviewBand);
    }

    [Fact]
    public async Task MatchAsync_AnAggregateUnderTheAcceptThreshold_IsNeverAcceptPassed_WhateverTheFrames()
    {
        using var scene = Build([0.84, 0.84, 0.84]);

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.False(report.AcceptPassed);
        Assert.True(report.InReviewBand);
    }

    [Fact]
    public async Task MatchAsync_TheThresholdsAreThePolicys_NotOnesOfTheCode()
    {
        using var scene = Build([0.9, 0.9, 0.9]);
        var matcher = Matcher(scene);
        var stricter = Policy with { Match = Policy.Match with { Accept = 0.95, MinEachFrame = 0.7 } };
        var demanding = Policy with { Match = Policy.Match with { MinEachFrame = 0.95 } };
        var narrower = Policy with { Match = Policy.Match with { ReviewFloor = 0.88, Accept = 0.95 } };
        var wider = Policy with { Match = Policy.Match with { ReviewFloor = 0.92, Accept = 0.95 } };

        Assert.True((await matcher.MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None)).AcceptPassed);
        Assert.False((await matcher.MatchAsync(scene.Document, scene.Sequence, scene.Indices, stricter, CancellationToken.None)).AcceptPassed);
        Assert.False((await matcher.MatchAsync(scene.Document, scene.Sequence, scene.Indices, demanding, CancellationToken.None)).AcceptPassed);
        Assert.True((await matcher.MatchAsync(scene.Document, scene.Sequence, scene.Indices, narrower, CancellationToken.None)).InReviewBand);
        Assert.False((await matcher.MatchAsync(scene.Document, scene.Sequence, scene.Indices, wider, CancellationToken.None)).InReviewBand);
    }

    // ─── Acceptance criterion 2: the review band ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.80, true)]
    [InlineData(0.72, true)]
    [InlineData(0.65, false)]
    [InlineData(0.20, false)]
    public async Task MatchAsync_TheReviewBandIsFromTheFloorToTheAcceptThreshold_AndIsReportedApart(double similarity, bool inBand)
    {
        using var scene = Build([similarity, similarity, similarity]);

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.Equal(MatchState.Ok, report.State);
        Assert.Equal(inBand, report.InReviewBand);
        Assert.False(report.AcceptPassed);
    }

    // ─── The similarity ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_TheSimilarityIsTheCosineOfTheCheckIn_ClampedToZeroAndOne()
    {
        // Three frames: a vector that is not of length 1 (the cosine does not care), the opposite of the portrait (a negative cosine counts as 0) and the portrait itself (1).
        using var scene = Build([0.5, 0.5, 0.5]);
        scene.Embedder.Replace(0, [3f, 4f, 0, 0, 0, 0, 0, 0]);
        scene.Embedder.Replace(1, [-1f, 0, 0, 0, 0, 0, 0, 0]);
        scene.Embedder.Replace(2, Portrait);
        var policy = Policy with { Match = Policy.Match with { Aggregation = IdentityAggregationRules.Min } };

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, policy, CancellationToken.None);

        var cosine = Math.Clamp(EmbeddingMath.CosineSimilarity(Portrait, [3f, 4f, 0, 0, 0, 0, 0, 0]), 0.0, 1.0);
        Assert.Equal(0.6, cosine, 6);
        Assert.Equal(0.0, report.Min, 6);
        var mean = Policy with { Match = Policy.Match with { Aggregation = IdentityAggregationRules.Mean } };
        var meanReport = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, mean, CancellationToken.None);
        Assert.Equal((0.6 + 0.0 + 1.0) / 3, meanReport.Aggregate, 6);
    }

    [Fact]
    public async Task MatchAsync_AFrameWithNoFace_CannotMatch_AndCountsAsZero()
    {
        using var scene = Build([0.95, null, 0.95]);

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.Equal(MatchState.Ok, report.State);
        Assert.Equal(3, report.FrameCount);
        Assert.Equal(0.95, report.Aggregate, 5);
        Assert.Equal(0.0, report.Min);
        Assert.False(report.AcceptPassed);

        // The embedder was shown the portrait and the two frames that have a face, in the order of the indices.
        Assert.Equal([-1, 0, 2], scene.Embedder.Embedded);
    }

    [Fact]
    public async Task MatchAsync_ComparesTheFramesTheServerChose_InTheOrderItGaveThem_WithTheThresholdsOfTheCheckIn()
    {
        using var scene = Build([0.9, 0.9, 0.9, 0.9, 0.9, 0.9]);

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, [4, 1, 5], Policy, CancellationToken.None);

        Assert.Equal(3, report.FrameCount);
        Assert.Equal([-1, 4, 1, 5], scene.Embedder.Embedded);

        // The document is looked for with the threshold of the documents, each frame with the one of the selfies.
        var options = new BiometricModelOptions();
        Assert.Equal(options.DocumentDetectionScoreThreshold, scene.Detector.Calls[0].MinScore);
        Assert.All(scene.Detector.Calls.Where(call => scene.Sequence.Frames.Any(frame => ReferenceEquals(frame.Image, call.Picture))), call => Assert.Equal(options.DetectionScoreThreshold, call.MinScore));
        Assert.Equal([4, 1, 5], scene.Detector.Calls.Select(call => scene.Sequence.Frames.ToList().FindIndex(frame => ReferenceEquals(frame.Image, call.Picture))).Where(index => index >= 0));
    }

    // ─── Acceptance criterion 4: no portrait, no model, no time ──────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_ADocumentWithNoFace_IsNoPortrait_NeverOk()
    {
        using var scene = Build([0.95, 0.95, 0.95], portrait: false);

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.Equal(MatchState.NoPortrait, report.State);
        Assert.Equal(MatchReport.NoPortrait(), report);
        Assert.False(report.AcceptPassed);
        Assert.False(report.InReviewBand);
        Assert.Equal(0, scene.Embedder.Calls);
    }

    [Theory]
    [InlineData("the models are not available")]
    [InlineData("the detector fails")]
    [InlineData("the embedder fails on the portrait")]
    [InlineData("the embedder fails on the second frame")]
    [InlineData("the time is over")]
    [InlineData("there is no policy")]
    [InlineData("the aggregation is not known")]
    [InlineData("there is no time limit")]
    public async Task MatchAsync_WhenAModelIsNotReadyFailsOrRunsOutOfTime_IsUnavailable_NeverOk(string situation)
    {
        using var scene = Build([0.95, 0.95, 0.95]);
        var logger = new CapturingLogger<PresenceMatcher>();
        var policy = Policy;
        var timeoutMs = 2500;
        switch (situation)
        {
            case "the models are not available":
                scene.Detector.Failure = new BiometricUnavailableException();
                break;
            case "the detector fails":
                scene.Detector.Failure = new InvalidOperationException("secret text of the detector");
                break;
            case "the embedder fails on the portrait":
                scene.Embedder.Failure = new InvalidDataException("secret text of the embedder");
                scene.Embedder.FailOnCall = 1;
                break;
            case "the embedder fails on the second frame":
                scene.Embedder.Failure = new InvalidDataException("secret text of the embedder");
                scene.Embedder.FailOnCall = 3;
                break;
            case "the time is over":
                scene.Detector.WaitForCancellation = true;
                timeoutMs = 50;
                break;
            case "there is no policy":
                policy = null!;
                break;
            case "the aggregation is not known":
                policy = Policy with { Match = Policy.Match with { Aggregation = "best" } };
                break;
            default:
                timeoutMs = 0;
                break;
        }

        var report = await Matcher(scene, timeoutMs, logger).MatchAsync(scene.Document, scene.Sequence, scene.Indices, policy, CancellationToken.None);

        Assert.Equal(MatchState.Unavailable, report.State);
        Assert.Equal(MatchReport.NotAvailable(), report);
        Assert.False(report.AcceptPassed);
        Assert.False(report.InReviewBand);
        Assert.DoesNotContain("secret", logger.AllOutput, StringComparison.Ordinal);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        if (situation is "there is no policy" or "the aggregation is not known" or "there is no time limit")
            Assert.Empty(scene.Detector.Calls);
    }

    // ─── Acceptance criterion 5: nothing of a face is left behind ────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_TheVectorsAreZeroedAfterTheComparison_AndTheFramesAndTheDocumentAreLeftAlone()
    {
        using var scene = Build([0.95, 0.9, 0.92]);

        await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        // The portrait and the three frames: four vectors handed out, every one of them zeroed.
        Assert.Equal(4, scene.Embedder.Issued.Count);
        Assert.All(scene.Embedder.Issued, vector => Assert.All(vector, value => Assert.Equal(0f, value)));

        // The pictures are the caller's: still there.
        Assert.Contains(scene.Document.Rgb, value => value != 0);
        Assert.All(scene.Sequence.Frames, frame => Assert.Contains(frame.Image.Rgb, value => value != 0));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task MatchAsync_WhenAnEmbeddingFails_TheVectorsAlreadyMade_AreZeroedToo(int failOnCall)
    {
        using var scene = Build([0.95, 0.95, 0.95]);
        scene.Embedder.Failure = new InvalidDataException("boom");
        scene.Embedder.FailOnCall = failOnCall;

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.Equal(MatchState.Unavailable, report.State);
        Assert.Equal(failOnCall - 1, scene.Embedder.Issued.Count);
        Assert.All(scene.Embedder.Issued, vector => Assert.All(vector, value => Assert.Equal(0f, value)));
    }

    /// <summary>
    /// Proof (privacy, ADR-013-R2): the copy of the document that the matcher turns upright is the picture of an identity document, and it is destroyed on every path. A document that came without its
    /// orientation has its face only in the copy turned a quarter (80 x 120 for the 120 x 80 picture), which the application makes itself: the embedder is shown that copy for the portrait, and after
    /// the match it has no pixel left, whether the match was made (0), the embedder failed on the portrait (1) or it failed on the second frame (3). The document itself is the caller's and is left alone.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task MatchAsync_TheRotatedCopyOfTheDocument_IsZeroedOnEveryPath(int failOnCall)
    {
        using var scene = Build([0.95, 0.9, 0.92], portrait: false);
        var turned = SequenceTestFrames.Face(80, 120);
        scene.Detector.Fallback = image => image.Width == 80 && image.Height == 120 ? [turned] : [];
        scene.Embedder.Unknown = Portrait;
        if (failOnCall > 0)
        {
            scene.Embedder.Failure = new InvalidDataException("secret text of the embedder");
            scene.Embedder.FailOnCall = failOnCall;
        }

        var report = await Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.Equal(failOnCall == 0 ? MatchState.Ok : MatchState.Unavailable, report.State);

        // The first picture the embedder was shown is the portrait's: a copy the application made (not the document), with its pixels while it was in use and nothing of them now.
        var rotated = scene.Embedder.Shown[0];
        Assert.NotSame(scene.Document, rotated);
        Assert.Equal((80, 120), (rotated.Width, rotated.Height));
        Assert.Equal(80 * 120 * 3, scene.Embedder.ShownLengths[0]);
        Assert.Empty(rotated.Rgb);

        // The document is the caller's: still there.
        Assert.Contains(scene.Document.Rgb, value => value != 0);
    }

    [Fact]
    public async Task MatchAsync_TheLog_HasTheStateAndTheNumberOfFramesOnly_NeverASimilarity()
    {
        using var scene = Build([0.8123, 0.9456, 0.7789]);
        var logger = new CapturingLogger<PresenceMatcher>();

        await Matcher(scene, logger: logger).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, CancellationToken.None);

        Assert.Equal(["Presence match: state=Ok, 3 frames"], logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotMatch(@"\d+[.,]\d+", logger.AllOutput);
    }

    // ─── The caller and its arguments ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_WhenTheCallerCancels_IsTheCancellation_NotAReport()
    {
        using var scene = Build([0.95, 0.95, 0.95]);
        scene.Detector.WaitForCancellation = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Matcher(scene).MatchAsync(scene.Document, scene.Sequence, scene.Indices, Policy, cancellation.Token));

        using var other = Build([0.95, 0.95, 0.95]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Matcher(other).MatchAsync(other.Document, other.Sequence, other.Indices, Policy, new CancellationToken(canceled: true)));
        Assert.Empty(other.Detector.Calls);
    }

    [Fact]
    public async Task MatchAsync_NothingToCompare_AFrameThatIsNotThere_AFrameTwice_OrAnotherNumberOfFrames_IsAnArgumentException()
    {
        using var scene = Build([0.95, 0.95, 0.95, 0.95]);
        var matcher = Matcher(scene);

        await Assert.ThrowsAsync<ArgumentException>(() => matcher.MatchAsync(scene.Document, scene.Sequence, [], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => matcher.MatchAsync(scene.Document, scene.Sequence, [0, 1, -1], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => matcher.MatchAsync(scene.Document, scene.Sequence, [0, 1, 4], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => matcher.MatchAsync(scene.Document, scene.Sequence, [0, 1, 1], Policy, CancellationToken.None));

        // The frozen rule is over the frames of the policy (three): two or four is another rule.
        await Assert.ThrowsAsync<ArgumentException>(() => matcher.MatchAsync(scene.Document, scene.Sequence, [0, 1], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => matcher.MatchAsync(scene.Document, scene.Sequence, [0, 1, 2, 3], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => matcher.MatchAsync(null!, scene.Sequence, [0, 1, 2], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => matcher.MatchAsync(scene.Document, null!, [0, 1, 2], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => matcher.MatchAsync(scene.Document, scene.Sequence, null!, Policy, CancellationToken.None));
        Assert.Empty(scene.Detector.Calls);
    }

    // ─── The registrations of the matching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registration_TheMatchingServices_HaveTheLifetimeOfWhatTheyTake_AndResolve()
    {
        // The selector and the matcher take nothing that lives for a request (one instance for the application); the liveness evaluator takes the anti-spoofing service of the check-in,
        // which is scoped, so it is too: a singleton would hold a scoped service for ever.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton<IFaceDetector>(new KeyedFaceDetector());
        services.AddSingleton<IFaceEmbedder>(new KeyedEmbedder());
        services.AddScoped(_ => Mock.Of<IAntiSpoofingService>());
        services.AddSelfHostedIdentityVerification();

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PresenceFrameSelector)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SequenceLivenessEvaluator)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(PresenceMatcher)).Lifetime);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SequenceLivenessEvaluator>());
        Assert.Same(provider.GetRequiredService<PresenceFrameSelector>(), scope.ServiceProvider.GetRequiredService<PresenceFrameSelector>());
        Assert.Same(provider.GetRequiredService<PresenceMatcher>(), scope.ServiceProvider.GetRequiredService<PresenceMatcher>());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The vector of the portrait: the first axis. A frame whose vector is [s, sqrt(1 - s^2)] has the similarity s with it.</summary>
    private static readonly float[] Portrait = [1f, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>What the embedder double records for the document (a frame is recorded by its place in the sequence).</summary>
    private const int DocumentPlace = -1;

    private static PresenceMatcher Matcher(Scene scene, int timeoutMs = 2500, CapturingLogger<PresenceMatcher>? logger = null)
    {
        return new PresenceMatcher(
            scene.Detector,
            scene.Embedder,
            Options.Create(new BiometricModelOptions { VerificationTimeoutMs = timeoutMs }),
            logger ?? new CapturingLogger<PresenceMatcher>());
    }

    /// <summary>The document, the frames the server chose, and the doubles of the models that know each picture: the face it has, and the vector it gives.</summary>
    private sealed class Scene : IDisposable
    {
        public required RasterImage Document { get; init; }

        public required FrameSequence Sequence { get; init; }

        public required KeyedFaceDetector Detector { get; init; }

        public required KeyedEmbedder Embedder { get; init; }

        /// <summary>The places of the first three frames, which are the ones a match of the policy compares.</summary>
        public int[] Indices => [.. Enumerable.Range(0, Math.Min(3, Sequence.Frames.Count))];

        public void Dispose()
        {
            Sequence.Dispose();
            Document.Dispose();
        }
    }

    /// <summary>
    /// A scene with a frame per similarity: a frame whose similarity is <c>null</c> has no face, one with a number has a face and a vector whose cosine with the portrait is that number.
    /// The document has a face, and the vector of the portrait, unless <paramref name="portrait"/> is false.
    /// </summary>
    private static Scene Build(IReadOnlyList<double?> similarities, bool portrait = true)
    {
        var detector = new KeyedFaceDetector();
        var embedder = new KeyedEmbedder();
        var frames = new List<SequenceFrame>(similarities.Count);
        for (var index = 0; index < similarities.Count; index++)
        {
            var image = Picture(96, 72, seed: 100 + index);
            frames.Add(new SequenceFrame(index, index * 125L, image, [0xFF, 0xD8, 0xFF, 0xE0, (byte)index]));
            if (similarities[index] is { } similarity)
            {
                detector.Add(image, SequenceTestFrames.Face(96, 72));
                embedder.Add(image, Frame(similarity), index);
            }
        }

        var document = Picture(120, 80, seed: 7);
        if (portrait)
        {
            detector.Add(document, SequenceTestFrames.Face(120, 80));
            embedder.Add(document, Portrait, DocumentPlace);
        }

        return new Scene
        {
            Document = document,
            Sequence = new FrameSequence(frames, SequenceTestFrames.IssuedAt),
            Detector = detector,
            Embedder = embedder,
        };
    }

    private static float[] Frame(double similarity)
    {
        var vector = new float[8];
        vector[0] = (float)similarity;
        vector[1] = (float)Math.Sqrt(Math.Max(0, 1 - (similarity * similarity)));
        return vector;
    }

    private static RasterImage Picture(int width, int height, int seed) => TestPictures.Noise(width, height, seed);

    /// <summary>
    /// An embedder that gives each picture the vector it was told (a fresh copy every time, kept in <see cref="Issued"/> to prove that the application zeroes it), records which pictures
    /// it was shown (<see cref="Embedded"/>: -1 for the document, the place in the sequence for a frame) and can fail on a call.
    /// </summary>
    private sealed class KeyedEmbedder : IFaceEmbedder
    {
        private readonly Dictionary<RasterImage, (float[] Vector, int Place)> _vectors = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, RasterImage> _pictures = [];

        public List<float[]> Issued { get; } = [];

        public List<int> Embedded { get; } = [];

        /// <summary>The pictures it was shown, in the order of the calls, those of a call that failed included.</summary>
        public List<RasterImage> Shown { get; } = [];

        /// <summary>How many bytes of pixels each picture of <see cref="Shown"/> had when it was shown (they are zeroed afterwards).</summary>
        public List<int> ShownLengths { get; } = [];

        /// <summary>The vector a picture that was not added gives: the copy of the document the application turns upright and makes itself, which no test holds.</summary>
        public float[]? Unknown { get; set; }

        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        /// <summary>The call (1-based) that fails with <see cref="Failure"/>; 0 makes every call fail once a failure is set.</summary>
        public int FailOnCall { get; set; }

        /// <summary>The vector the picture at <paramref name="place"/> (<see cref="DocumentPlace"/> for the document, the place in the sequence for a frame) gives.</summary>
        public void Add(RasterImage picture, float[] vector, int place)
        {
            _vectors[picture] = (vector, place);
            _pictures[place] = picture;
        }

        /// <summary>Gives the picture at <paramref name="place"/> another vector.</summary>
        public void Replace(int place, float[] vector)
        {
            _vectors[_pictures[place]] = (vector, place);
        }

        public float[] Embed(RasterImage image, DetectedFace face, CancellationToken cancellationToken)
        {
            Calls++;
            Shown.Add(image);
            ShownLengths.Add(image.Rgb.Length);
            if (Failure is not null && (FailOnCall == 0 || Calls == FailOnCall))
                throw Failure;

            float[] vector;
            int place;
            if (_vectors.TryGetValue(image, out var known))
                (vector, place) = known;
            else if (Unknown is { } turned)
                (vector, place) = (turned, DocumentPlace);
            else
                throw new KeyNotFoundException("The embedder double does not know this picture.");

            Embedded.Add(place);
            var issued = (float[])vector.Clone();
            Issued.Add(issued);
            return issued;
        }
    }
}
