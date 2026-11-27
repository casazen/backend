using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E72/F01/US02/T01 (NF-192, NF-193, ADR-013-R17, decision D6): the liveness of the frames the server chose. It passes only when the rule of the policy holds (<c>all</c>: every chosen
/// frame reaches <c>presence.liveness.minScore</c>, the threshold of the policy and not the one of the anti-spoofing service); a model that is not ready, does not score a frame,
/// fails or runs out of time is <see cref="LivenessState.Unavailable"/>, never a pass; nothing of a frame or a score reaches the log. The anti-spoofing service is a double that
/// answers from a script, and once the real one over the bundled MiniFASNetV2.
/// </summary>
public class SequenceLivenessEvaluatorTests
{
    private static readonly IdentityDecisionPolicy Policy = IdentityTestPolicy.Shipped();

    private static readonly int[] Indices = [4, 20, 33];

    // ─── The proof of the brief ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (security, NF-192): a fault of the model never passes. Whatever goes wrong with the model (it does not run, it throws, it says a score that is not one, it never
    /// answers, or fails after other frames passed), with no policy, or with a rule the code does not know: the liveness is <see cref="LivenessState.Unavailable"/>, there is no
    /// partial answer, and it is never a pass.
    /// </summary>
    [Theory]
    [InlineData("the model did not run")]
    [InlineData("the model is not available")]
    [InlineData("the model throws")]
    [InlineData("the score is NaN")]
    [InlineData("the score is above 1")]
    [InlineData("the score is negative")]
    [InlineData("the model never answers")]
    [InlineData("the model fails after two frames passed")]
    [InlineData("there is no policy")]
    [InlineData("the rule is not known")]
    [InlineData("there is no time limit")]
    public async Task SequenceLivenessEvaluator_ModelUnavailable_IsUnavailable(string situation)
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        var service = new ScriptedAntiSpoofing(Score(0.99), Score(0.99), Score(0.99));
        var policy = Policy;
        var timeoutMs = 1500;
        switch (situation)
        {
            case "the model did not run":
                service = new ScriptedAntiSpoofing(default(LivenessResult), Score(0.99), Score(0.99));
                break;
            case "the model is not available":
                service.Failure = new BiometricUnavailableException();
                break;
            case "the model throws":
                service.Failure = new InvalidOperationException("secret text of the model");
                break;
            case "the score is NaN":
                service = new ScriptedAntiSpoofing(Score(0.99), new LivenessResult(true, double.NaN), Score(0.99));
                break;
            case "the score is above 1":
                service = new ScriptedAntiSpoofing(Score(0.99), Score(1.5), Score(0.99));
                break;
            case "the score is negative":
                service = new ScriptedAntiSpoofing(Score(0.99), Score(-0.2), Score(0.99));
                break;
            case "the model never answers":
                service.WaitForCancellation = true;
                timeoutMs = 50;
                break;
            case "the model fails after two frames passed":
                service = new ScriptedAntiSpoofing(Score(0.99), Score(0.99)) { FailOnCall = 3 };
                break;
            case "there is no policy":
                policy = null!;
                break;
            case "the rule is not known":
                policy = Policy with { Presence = Policy.Presence with { Liveness = Policy.Presence.Liveness with { Rule = "any" } } };
                break;
            default:
                timeoutMs = 0;
                break;
        }

        var report = await Evaluator(service, timeoutMs).EvaluateAsync(run.Sequence, Indices, policy, CancellationToken.None);

        AssertNotAvailable(report);
        if (situation is "there is no policy" or "the rule is not known" or "there is no time limit")
            Assert.Equal(0, service.Calls);
    }

    // ─── Acceptance criterion 4: the rule of the policy ──────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_EveryChosenFrameAtTheMinimumOrAbove_Passes_WithTheVeryFramesThatWereChosen()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        var service = new ScriptedAntiSpoofing(Score(0.7), Score(0.93), Score(0.7001));

        var report = await Evaluator(service).EvaluateAsync(run.Sequence, Indices, Policy, CancellationToken.None);

        // The minimum is reached by "at least" (0.7 passes).
        Assert.True(report.Passed);
        Assert.Equal(LivenessState.Passed, report.State);
        Assert.False(report.IsUnavailable);
        Assert.Equal(Indices, report.Frames.Select(frame => frame.Index));
        Assert.Equal([0.7, 0.93, 0.7001], report.Frames.Select(frame => frame.Score));
        Assert.Equal(0.7, report.MinScore);

        // The service was given the bytes the sequence holds for each chosen frame, in the order of the indices, and they are still there.
        Assert.Equal(Indices.Select(index => run.Sequence.Frames[index].Jpeg), service.Seen);
        Assert.All(Indices, index => Assert.Contains(run.Sequence.Frames[index].Jpeg, value => value != 0));
    }

    [Fact]
    public async Task EvaluateAsync_OneFrameUnderTheMinimum_Fails_AndEveryFrameIsScored()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        var service = new ScriptedAntiSpoofing(Score(0.95), Score(0.69), Score(0.99));

        var report = await Evaluator(service).EvaluateAsync(run.Sequence, Indices, Policy, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.False(report.IsUnavailable);
        Assert.Equal(LivenessState.Failed, report.State);
        Assert.Equal(3, service.Calls);
        Assert.Equal([0.95, 0.69, 0.99], report.Frames.Select(frame => frame.Score));
        Assert.Equal(0.69, report.MinScore);
    }

    [Fact]
    public async Task EvaluateAsync_TheMinimumIsThePolicys_NotTheThresholdOfTheService()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);

        // The service says "not live" for 0.9 (its own threshold is higher) and "live" for 0.5: neither decides.
        var high = new ScriptedAntiSpoofing(new LivenessResult(false, 0.9), new LivenessResult(false, 0.9), new LivenessResult(false, 0.9));
        var low = new ScriptedAntiSpoofing(new LivenessResult(true, 0.5), new LivenessResult(true, 0.5), new LivenessResult(true, 0.5));
        var lenient = Policy with { Presence = Policy.Presence with { Liveness = Policy.Presence.Liveness with { MinScore = 0.5 } } };

        Assert.True((await Evaluator(high).EvaluateAsync(run.Sequence, Indices, Policy, CancellationToken.None)).Passed);
        Assert.False((await Evaluator(low).EvaluateAsync(run.Sequence, Indices, Policy, CancellationToken.None)).Passed);
        Assert.True((await Evaluator(new ScriptedAntiSpoofing(Score(0.5), Score(0.5), Score(0.5))).EvaluateAsync(run.Sequence, Indices, lenient, CancellationToken.None)).Passed);
    }

    [Fact]
    public async Task EvaluateAsync_ASingleFrameIsJudgedLikeAnyOther()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);

        var report = await Evaluator(new ScriptedAntiSpoofing(Score(0.72))).EvaluateAsync(run.Sequence, [10], Policy, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal(10, Assert.Single(report.Frames).Index);
    }

    // ─── The caller and its arguments ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_WhenTheCallerCancels_IsTheCancellation_NotAReport()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        var service = new ScriptedAntiSpoofing(Score(0.9), Score(0.9), Score(0.9)) { WaitForCancellation = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Evaluator(service).EvaluateAsync(run.Sequence, Indices, Policy, cancellation.Token));

        var untouched = new ScriptedAntiSpoofing(Score(0.9));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Evaluator(untouched).EvaluateAsync(run.Sequence, [1], Policy, new CancellationToken(canceled: true)));
        Assert.Equal(0, untouched.Calls);
    }

    [Fact]
    public async Task EvaluateAsync_NothingToJudge_OrAFrameThatIsNotThere_OrAFrameTwice_IsAnArgumentException_NeverAPass()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        var service = new ScriptedAntiSpoofing(Score(0.9), Score(0.9));
        var evaluator = Evaluator(service);

        await Assert.ThrowsAsync<ArgumentException>(() => evaluator.EvaluateAsync(run.Sequence, [], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => evaluator.EvaluateAsync(run.Sequence, [3, -1], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => evaluator.EvaluateAsync(run.Sequence, [3, run.Sequence.Frames.Count], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => evaluator.EvaluateAsync(run.Sequence, [3, 3], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.EvaluateAsync(null!, [3], Policy, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.EvaluateAsync(run.Sequence, null!, Policy, CancellationToken.None));
        Assert.Equal(0, service.Calls);
    }

    // ─── Privacy ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_TheLog_HasTheStateAndTheNumberOfFramesOnly_NeverAScore()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        var logger = new CapturingLogger<SequenceLivenessEvaluator>();

        await Evaluator(new ScriptedAntiSpoofing(Score(0.8123), Score(0.9456), Score(0.7789)), logger: logger).EvaluateAsync(run.Sequence, Indices, Policy, CancellationToken.None);
        await Evaluator(new ScriptedAntiSpoofing(Score(0.8123), Score(0.6112), Score(0.7789)), logger: logger).EvaluateAsync(run.Sequence, Indices, Policy, CancellationToken.None);

        Assert.Equal(
            ["Presence liveness judged: passed=True, 3 frames", "Presence liveness judged: passed=False, 3 frames"],
            logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotMatch(@"\d+[.,]\d+", logger.AllOutput);
    }

    [Fact]
    public async Task EvaluateAsync_WhenTheModelFails_TheMessageOfTheExceptionIsNotLogged()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        var logger = new CapturingLogger<SequenceLivenessEvaluator>();
        var service = new ScriptedAntiSpoofing(Score(0.9)) { Failure = new InvalidOperationException("secret text of the model") };

        var report = await Evaluator(service, logger: logger).EvaluateAsync(run.Sequence, Indices, Policy, CancellationToken.None);

        AssertNotAvailable(report);
        Assert.DoesNotContain("secret", logger.AllOutput, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), logger.AllOutput, StringComparison.Ordinal);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    // ─── With the real model ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_WithTheBundledModel_GivesTheScoresTheServiceGives()
    {
        // The real anti-spoofing service over the bundled MiniFASNetV2, with a detector double that finds the same face in every picture: generated JPEG frames, no photograph.
        const int width = 160;
        const int height = 120;
        using var store = await BundledBiometricModels.LoadAsync();
        Assert.True(store.IsAvailable);
        var options = Options.Create(BundledBiometricModels.PublishedOptions());
        var face = SequenceTestFrames.Face(width, height);
        var frames = Enumerable.Range(0, 3)
            .Select(index => new SequenceFrame(index, index * 125L, new RasterImage(width, height, new byte[width * height * 3]), TestJpeg.Noise(width, height, 40 + index)))
            .ToList();
        using var sequence = new FrameSequence(frames, SequenceTestFrames.IssuedAt);
        var detector = new ScriptedFaceDetector([face], [face], [face], [face], [face], [face]);
        var serviceLog = new CapturingLogger<AntiSpoofingService>();
        var service = new AntiSpoofingService(serviceLog, store, detector, options);
        var evaluator = new SequenceLivenessEvaluator(service, options, NullLogger<SequenceLivenessEvaluator>.Instance);

        var report = await evaluator.EvaluateAsync(sequence, [0, 2], Policy, CancellationToken.None);

        // The service says whether each frame is live and never its score: the frames of the self-hosted presence go through it one by one (ADR-013-R17, no number of a person in a log).
        Assert.Equal(2, serviceLog.Entries.Count);
        Assert.DoesNotMatch(@"\d+[.,]\d+", serviceLog.AllOutput);

        Assert.False(report.IsUnavailable);
        Assert.Equal([0, 2], report.Frames.Select(frame => frame.Index));
        Assert.All(report.Frames, frame => Assert.InRange(frame.Score, 1e-12, 1.0));
        Assert.Equal(report.Frames.All(frame => frame.Score >= Policy.Presence.Liveness.MinScore), report.Passed);

        // The service itself, on the same bytes, gives the same scores: the evaluator adds no number of its own.
        foreach (var scored in report.Frames)
        {
            using var picture = new MemoryStream(sequence.Frames[scored.Index].Jpeg, writable: false);
            var direct = await service.ScoreLivenessAsync(picture);
            Assert.Equal(direct.Score, scored.Score, 9);
        }
    }

    [Fact]
    public async Task EvaluateAsync_WithTheBundledModelNotReady_IsUnavailable()
    {
        // A store that has not loaded anything: the real service answers "not live, score 0", which is a model that did not run.
        var options = Options.Create(new BiometricModelOptions());
        var service = new AntiSpoofingService(NullLogger<AntiSpoofingService>.Instance, new FakeBiometricModelStore(available: false), new ScriptedFaceDetector(), options);
        var evaluator = new SequenceLivenessEvaluator(service, options, NullLogger<SequenceLivenessEvaluator>.Instance);
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);

        var report = await evaluator.EvaluateAsync(run.Sequence, Indices, Policy, CancellationToken.None);

        AssertNotAvailable(report);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static LivenessResult Score(double score) => new(true, score);

    private static SequenceLivenessEvaluator Evaluator(ScriptedAntiSpoofing service, int timeoutMs = 1500, CapturingLogger<SequenceLivenessEvaluator>? logger = null)
    {
        return new SequenceLivenessEvaluator(
            service,
            Options.Create(new BiometricModelOptions { LivenessTimeoutMs = timeoutMs }),
            logger ?? new CapturingLogger<SequenceLivenessEvaluator>());
    }

    /// <summary>A liveness that could not be judged: never a pass, no partial answer.</summary>
    private static void AssertNotAvailable(LivenessReport report)
    {
        Assert.False(report.Passed);
        Assert.True(report.IsUnavailable);
        Assert.Equal(LivenessState.Unavailable, report.State);
        Assert.Empty(report.Frames);
        Assert.Null(report.MinScore);
    }

    /// <summary>An anti-spoofing service that answers from a script and keeps the bytes of every picture it was given; it can fail, wait or fail at a call.</summary>
    private sealed class ScriptedAntiSpoofing(params LivenessResult[] results) : IAntiSpoofingService
    {
        private readonly Queue<LivenessResult> _results = new(results);

        public List<byte[]> Seen { get; } = [];

        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        /// <summary>The call (1-based) that fails with an exception.</summary>
        public int FailOnCall { get; set; }

        public bool WaitForCancellation { get; set; }

        public Task<bool> DetectLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<LivenessResult> ScoreLivenessAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            Calls++;
            using var copy = new MemoryStream();
            await imageStream.CopyToAsync(copy, cancellationToken);
            Seen.Add(copy.ToArray());

            if (WaitForCancellation)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            if (Failure is not null || Calls == FailOnCall)
                throw Failure ?? new InvalidDataException("secret text of the model");

            return _results.Dequeue();
        }
    }
}
