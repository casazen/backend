using System.Reflection;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E72/F01/US02/T01 (NF-192, NF-193, ADR-013-R17, decision D6): the server chooses the frames of a validated presence sequence. Only frames the validation saw with one face, good
/// quality and a near-frontal pose can be compared with the portrait; the choice is drawn at random among the best candidates so the client cannot know which frames decide, and it
/// always keeps the gap of the policy; the frames the liveness is judged on include the match frames, are spread over the whole sequence and include one inside the moves of the
/// challenge; a report that did not pass is refused; not enough frames is the failure <c>presence_quality</c>. Every number is the committed policy's; the faces, the poses and the
/// pictures are written in code (no photograph: the repository is public).
/// </summary>
public class PresenceFrameSelectorTests
{
    private static readonly IdentityDecisionPolicy Policy = IdentityTestPolicy.Shipped();

    private const int Width = 96;
    private const int Height = 72;

    // ─── The proof of the brief ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (security, NF-192, NF-193): the client cannot choose the frames. The sequence of a genuine guest, validated by the real validation, with the defects it tolerates (four
    /// frames without a face, three too dark to judge): over many selections, with the draw of the application, the frames chosen for the match are only frames the validation saw
    /// with one face, good quality and a near-frontal pose; the ones chosen for the liveness only frames it could judge; the gap of the policy is always kept; and the choice is not the
    /// same every time.
    /// </summary>
    [Fact]
    public async Task PresenceFrameSelector_ChoosesOnlyValidatedFramesAtRandomAmongTheBest()
    {
        var script = SequenceTestFrames.Genuine(Policy);
        foreach (var index in new[] { 3, 12, 30, 41 })
            script.Frames[index].Faces = [];

        foreach (var index in new[] { 15, 22, 36 })
            script.Frames[index].Gray = new byte[script.Width * script.Height];

        using var run = script.Run(Policy);
        var report = await Validator(run).ValidateAsync(run.Sequence, run.Challenge, Policy, CancellationToken.None);
        Assert.True(report.Passed, "the sequence of the script must pass its validation");

        var usable = report.Frames.Where(frame => frame.FaceCount == 1 && frame.QualityOk).Select(frame => frame.Index).ToHashSet();
        var nearFront = report.Frames
            .Where(frame => frame.FaceCount == 1 && frame.QualityOk && Math.Abs(frame.YawDegrees) <= 15 && Math.Abs(frame.PitchDegrees) <= 15)
            .Select(frame => frame.Index)
            .ToHashSet();
        Assert.DoesNotContain(usable, index => index is 3 or 12 or 30 or 41 or 15 or 22 or 36);
        Assert.True(nearFront.Count > 10);

        var selector = new PresenceFrameSelector();
        var selections = new HashSet<string>();
        for (var round = 0; round < 200; round++)
        {
            var selection = selector.Select(run.Sequence, report, Policy);

            Assert.True(selection.IsSelected);
            var frames = selection.Frames;
            Assert.All(frames.MatchIndices, index => Assert.Contains(index, nearFront));
            Assert.All(frames.LivenessIndices, index => Assert.Contains(index, usable));
            Assert.Equal(Policy.Match.Frames, frames.MatchIndices.Count);
            AssertSortedAndApart(frames.MatchIndices, run.Sequence, Policy.Match.MinFrameGapMs);
            Assert.Equal(Policy.Presence.Liveness.Frames, frames.LivenessIndices.Count);
            AssertLivenessFrames(frames, run.Sequence, report, Policy);
            selections.Add(string.Join(',', frames.MatchIndices) + "|" + string.Join(',', frames.LivenessIndices));
        }

        Assert.True(selections.Count > 1, "two runs on the same sequence must be able to choose different frames");
    }

    [Fact]
    public void PresenceFrameSelector_TheClientHasNoSayInTheChoice_NoParameterNamesFrames()
    {
        // What goes in is the sequence (the server's own copy of the frames), the report of its validation and the policy: no list of places, no time, nothing the page sends.
        var parameters = typeof(PresenceFrameSelector).GetMethod(nameof(PresenceFrameSelector.Select))!.GetParameters().Select(parameter => parameter.ParameterType);
        Assert.Equal([typeof(FrameSequence), typeof(SequenceValidationReport), typeof(IdentityDecisionPolicy)], parameters);

        // The only constructor of the application draws with RandomNumberGenerator and takes nothing; it holds no logger, so no ranking number can reach a log.
        var constructor = Assert.Single(typeof(PresenceFrameSelector).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(constructor.GetParameters());
        Assert.DoesNotContain(
            typeof(PresenceFrameSelector).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static),
            field => typeof(ILogger).IsAssignableFrom(field.FieldType));
    }

    // ─── Acceptance criteria 1 and 2: who can be chosen, and how ─────────────────────────────────────────

    [Fact]
    public void Select_OnlyFramesWithOneFaceGoodQualityAndANearFrontalPose_CanBeChosenForTheMatch()
    {
        // Eight kinds of frame, in rotation: neutral, poor quality, two faces, no face, turned 15.5 degrees, looking down 15.5 degrees, a pose that was not estimated, and turned exactly
        // 15 degrees (the limit of the policy, which is allowed).
        using var built = Build(
            40,
            (index, spec) =>
            {
                switch (index % 8)
                {
                    case 1:
                        spec.QualityOk = false;
                        break;
                    case 2:
                        spec.FaceCount = 2;
                        break;
                    case 3:
                        spec.FaceCount = 0;
                        break;
                    case 4:
                        spec.Yaw = 15.5;
                        break;
                    case 5:
                        spec.Pitch = -15.5;
                        break;
                    case 6:
                        spec.Yaw = double.NaN;
                        break;
                    case 7:
                        spec.Yaw = 15;
                        break;
                }
            });

        var matchable = Enumerable.Range(0, 40).Where(index => index % 8 is 0 or 7).ToHashSet();
        var judgeable = Enumerable.Range(0, 40).Where(index => index % 8 is 0 or 4 or 5 or 6 or 7).ToHashSet();
        var selector = new PresenceFrameSelector();
        for (var round = 0; round < 200; round++)
        {
            var selection = selector.Select(built.Sequence, built.Report, Policy);

            Assert.True(selection.IsSelected);
            Assert.All(selection.Frames.MatchIndices, index => Assert.Contains(index, matchable));
            Assert.All(selection.Frames.LivenessIndices, index => Assert.Contains(index, judgeable));
        }
    }

    [Fact]
    public void Select_EachPickIsDrawnAmongTheBestRemainingCandidates_AndKeepsTheGap()
    {
        // 30 frames 125 ms apart, all neutral, the face growing from one frame to the next: the best candidate is always the last frame that remains.
        using var built = Build(30, (index, spec) => spec.WidthShare = 0.20 + (0.01 * index));
        var draws = new List<int>();

        // Always the best of the two: 29, then 26 (29 and 28 are too near), then 23.
        var best = new PresenceFrameSelector(count =>
        {
            draws.Add(count);
            return 0;
        }).Select(built.Sequence, built.Report, Policy);

        Assert.True(best.IsSelected);
        Assert.Equal([23, 26, 29], best.Frames.MatchIndices);

        // Always the second of the two: 28, then 24 (the best two that remain are 25 and 24), then 20.
        var second = new PresenceFrameSelector(count => count - 1).Select(built.Sequence, built.Report, Policy);

        Assert.True(second.IsSelected);
        Assert.Equal([20, 24, 28], second.Frames.MatchIndices);

        // The draw is never asked for more than the number of the policy (the best two), and a frame is asked for once per pick of the match.
        Assert.All(draws.Take(Policy.Match.Frames), count => Assert.InRange(count, 1, Policy.Match.RandomizeAmongTop));
    }

    [Fact]
    public void Select_AFrameIsNeverOfferedToTheDrawWhenTheRestCouldNotBePicked()
    {
        // Three frames 200 ms apart, the middle one the best, a gap of 300 ms and two frames to pick: picking the middle one first would leave nothing (the others are 200 ms away),
        // so the only choice is the first and the last, whatever the draw.
        var policy = Policy with
        {
            Match = Policy.Match with { Frames = 2, MinFrameGapMs = 300, RandomizeAmongTop = 2 },
            Presence = Policy.Presence with
            {
                Challenge = Policy.Presence.Challenge with { LeadInMs = 0, StepMs = 1000 },
                Liveness = Policy.Presence.Liveness with { Frames = 2 },
            },
        };
        using var built = Build(3, (index, spec) => spec.WidthShare = index == 1 ? 0.5 : 0.3, interval: 200);

        for (var round = 0; round < 100; round++)
        {
            var selection = new PresenceFrameSelector().Select(built.Sequence, built.Report, policy);

            Assert.True(selection.IsSelected);
            Assert.Equal([0, 2], selection.Frames.MatchIndices);
        }

        Assert.Equal([0, 2], new PresenceFrameSelector(_ => 0).Select(built.Sequence, built.Report, policy).Frames!.MatchIndices);
    }

    [Theory]
    [InlineData(4, 300)]
    [InlineData(2, 300)]
    [InlineData(3, 800)]
    [InlineData(3, 0)]
    public void Select_TheCountAndTheGapAreThePolicys(int frames, int gapMs)
    {
        var policy = Policy with { Match = Policy.Match with { Frames = frames, MinFrameGapMs = gapMs } };
        using var built = Build(60, (index, spec) => spec.WidthShare = 0.2 + (0.004 * index));

        var selection = new PresenceFrameSelector().Select(built.Sequence, built.Report, policy);

        Assert.True(selection.IsSelected);
        Assert.Equal(frames, selection.Frames.MatchIndices.Count);
        AssertSortedAndApart(selection.Frames.MatchIndices, built.Sequence, gapMs);
    }

    [Fact]
    public void Select_ANearFrontalPoseIsTheOneOfThePolicy_NotOneOfTheCode()
    {
        // Frames turned 10 degrees are near-frontal for a policy that allows 15 and not for one that allows 5: with 5 only the three frames at rest are left.
        var strict = Policy with { Presence = Policy.Presence with { Pose = Policy.Presence.Pose with { NeutralMaxDegrees = 5 } } };
        using var built = Build(40, (index, spec) => spec.Yaw = index is 5 or 15 or 25 ? 0 : 10);

        var lenient = new PresenceFrameSelector().Select(built.Sequence, built.Report, Policy);
        var narrow = new PresenceFrameSelector().Select(built.Sequence, built.Report, strict);

        Assert.True(lenient.IsSelected);
        Assert.True(narrow.IsSelected);
        Assert.Equal([5, 15, 25], narrow.Frames.MatchIndices);
    }

    // ─── Acceptance criterion 3: the liveness frames ─────────────────────────────────────────────────────

    [Fact]
    public void Select_TheLivenessFramesAreTheMatchFramesAndOthersSpreadOverTheSequence()
    {
        using var built = Build(30, (index, spec) => spec.WidthShare = 0.20 + (0.01 * index));

        var selection = new PresenceFrameSelector(_ => 0).Select(built.Sequence, built.Report, Policy);

        Assert.True(selection.IsSelected);
        Assert.Equal([23, 26, 29], selection.Frames.MatchIndices);

        // The three frames of the match, and then the farthest in time from them: the first frame and one in the first half of the sequence.
        Assert.Equal([0, 11, 23, 26, 29], selection.Frames.LivenessIndices);
        AssertLivenessFrames(selection.Frames, built.Sequence, built.Report, Policy);
    }

    [Fact]
    public void Select_WhenNoMatchFrameIsInsideTheMoves_OneLivenessFrameIs()
    {
        // The head is at rest only during the lead-in (the first second): every frame later is turned 25 degrees, so the match frames are all before the first move, and the
        // liveness must add a frame inside the moves (from the end of the lead-in to the end of the last step).
        using var built = Build(51, (index, spec) =>
        {
            spec.Yaw = index * 125 < Policy.Presence.Challenge.LeadInMs ? 0 : 25;
            spec.WidthShare = 0.2 + (0.005 * index);
        });
        var selector = new PresenceFrameSelector();

        for (var round = 0; round < 100; round++)
        {
            var selection = selector.Select(built.Sequence, built.Report, Policy);

            Assert.True(selection.IsSelected);
            Assert.All(selection.Frames.MatchIndices, index => Assert.True(index * 125 < Policy.Presence.Challenge.LeadInMs));
            AssertLivenessFrames(selection.Frames, built.Sequence, built.Report, Policy);
            Assert.Equal(Policy.Presence.Liveness.Frames, selection.Frames.LivenessIndices.Count);
        }
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(9, 9)]
    public void Select_TheNumberOfLivenessFramesIsThePolicys_ButNeverLessThanTheMatchFrames(int livenessFrames, int expected)
    {
        var policy = Policy with { Presence = Policy.Presence with { Liveness = Policy.Presence.Liveness with { Frames = livenessFrames } } };
        using var built = Build(51, (index, spec) => spec.WidthShare = 0.2 + (0.005 * index));

        var selection = new PresenceFrameSelector().Select(built.Sequence, built.Report, policy);

        Assert.True(selection.IsSelected);

        // With as many liveness frames as match frames the set is the match frames, plus one when none of them is inside the moves: never fewer than what is compared.
        Assert.InRange(selection.Frames.LivenessIndices.Count, expected, expected + 1);
        AssertLivenessFrames(selection.Frames, built.Sequence, built.Report, policy);
    }

    // ─── Acceptance criterion 5: a report that did not pass ──────────────────────────────────────────────

    [Fact]
    public void Select_AReportThatDidNotPass_IsRefused_AndNothingIsDrawn()
    {
        using var built = Build(40);
        var draws = 0;
        var selector = new PresenceFrameSelector(count =>
        {
            draws++;
            return 0;
        });

        var failed = built.Report with
        {
            Passed = false,
            Failures = [new SequenceFailure(IdentityGuidanceCodes.PresenceChallengeNotFollowed, VerificationReasons.Checks.PresenceChallenge, Correctable: true)],
        };
        var contradiction = built.Report with { Failures = [new SequenceFailure(IdentityGuidanceCodes.PresenceTiming, VerificationReasons.Checks.PresenceTiming, Correctable: false)] };
        var sequenceOfAnother = built.Report with { Frames = [.. built.Report.Frames.Take(39)] };
        var timesOfAnother = built.Report with { Frames = [.. built.Report.Frames.Select(frame => frame with { TimestampMs = frame.TimestampMs + 1 })] };

        Assert.Throws<InvalidOperationException>(() => selector.Select(built.Sequence, failed, Policy));
        Assert.Throws<InvalidOperationException>(() => selector.Select(built.Sequence, SequenceValidationReport.NotAvailable(), Policy));
        Assert.Throws<InvalidOperationException>(() => selector.Select(built.Sequence, contradiction, Policy));
        Assert.Throws<InvalidOperationException>(() => selector.Select(built.Sequence, sequenceOfAnother, Policy));
        Assert.Throws<InvalidOperationException>(() => selector.Select(built.Sequence, timesOfAnother, Policy));
        Assert.Equal(0, draws);

        // The positive control: the same sequence with the report that passed.
        Assert.True(selector.Select(built.Sequence, built.Report, Policy).IsSelected);
        Assert.True(draws > 0);
    }

    [Fact]
    public void Select_WithoutTheArguments_IsAnArgumentNullException()
    {
        using var built = Build(40);
        var selector = new PresenceFrameSelector();

        Assert.Throws<ArgumentNullException>(() => selector.Select(null!, built.Report, Policy));
        Assert.Throws<ArgumentNullException>(() => selector.Select(built.Sequence, null!, Policy));
        Assert.Throws<ArgumentNullException>(() => selector.Select(built.Sequence, built.Report, null!));
    }

    // ─── Not enough frames: presence_quality ─────────────────────────────────────────────────────────────

    [Fact]
    public void Select_WithFewerNearFrontalFramesThanThePolicyAsks_IsThePresenceQualityFailure()
    {
        // Only two frames at rest: the policy compares three.
        using var built = Build(40, (index, spec) => spec.Yaw = index is 5 or 20 ? 0 : 30);

        var selection = new PresenceFrameSelector().Select(built.Sequence, built.Report, Policy);

        AssertPresenceQuality(selection);
    }

    [Fact]
    public void Select_WhenNoSetOfFramesRespectsTheGap_IsThePresenceQualityFailure()
    {
        // Frames at rest 125 ms apart, but only four of them in a window of 375 ms: three frames 300 ms apart do not exist.
        using var built = Build(40, (index, spec) => spec.Yaw = index < 4 ? 0 : 30);

        var selection = new PresenceFrameSelector().Select(built.Sequence, built.Report, Policy);

        AssertPresenceQuality(selection);
    }

    [Fact]
    public void Select_WithoutAUsableFrameInsideTheMoves_IsThePresenceQualityFailure()
    {
        // The frames at rest are before the moves and every frame inside them is too dark to be judged.
        using var built = Build(
            51,
            (index, spec) =>
            {
                spec.Yaw = index * 125 < Policy.Presence.Challenge.LeadInMs ? 0 : 30;
                spec.QualityOk = index * 125 < Policy.Presence.Challenge.LeadInMs || index * 125 > 5500;
            });

        var selection = new PresenceFrameSelector().Select(built.Sequence, built.Report, Policy);

        AssertPresenceQuality(selection);
    }

    [Fact]
    public void Select_WithFewerFramesToJudgeThanTheLivenessAsks_IsThePresenceQualityFailure()
    {
        // The policy judges five frames and only four can be judged (the others are too dark): fewer frames than the frozen rule is never accepted.
        using var built = Build(51, (index, spec) => spec.QualityOk = index is 0 or 3 or 6 or 9);

        var selection = new PresenceFrameSelector().Select(built.Sequence, built.Report, Policy);

        AssertPresenceQuality(selection);
    }

    [Fact]
    public void Select_DoesNotTouchTheFrames()
    {
        using var built = Build(40, (index, spec) => spec.WidthShare = 0.2 + (0.005 * index));
        var pixels = built.Sequence.Frames.Select(frame => frame.Image.Rgb.ToArray()).ToList();
        var jpegs = built.Sequence.Frames.Select(frame => frame.Jpeg.ToArray()).ToList();

        new PresenceFrameSelector().Select(built.Sequence, built.Report, Policy);

        for (var index = 0; index < pixels.Count; index++)
        {
            Assert.Equal(pixels[index], built.Sequence.Frames[index].Image.Rgb);
            Assert.Equal(jpegs[index], built.Sequence.Frames[index].Jpeg);
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static PresenceSequenceValidator Validator(ScriptedRun run)
    {
        return new PresenceSequenceValidator(
            run.Detector,
            run.Embedder,
            Options.Create(new BiometricModelOptions()),
            Options.Create(new IdentityVerificationOptions()),
            NullLogger<PresenceSequenceValidator>.Instance);
    }

    private static void AssertPresenceQuality(FrameSelection selection)
    {
        Assert.False(selection.IsSelected);
        Assert.Null(selection.Frames);
        var failure = Assert.IsType<SequenceFailure>(selection.Failure);
        Assert.Equal(
            (IdentityGuidanceCodes.PresenceQuality, VerificationReasons.Checks.PresenceQuality, true, VerificationReasons.Results.Failed),
            (failure.Code, failure.Check, failure.Correctable, failure.Result));
        Assert.Equal("presence_quality", failure.Code);
    }

    /// <summary>The indices are in time order and every two of them are at least <paramref name="gapMs"/> apart.</summary>
    private static void AssertSortedAndApart(IReadOnlyList<int> indices, FrameSequence sequence, int gapMs)
    {
        Assert.Equal(indices.Order(), indices);
        Assert.Equal(indices.Count, indices.Distinct().Count());
        for (var left = 0; left < indices.Count; left++)
        {
            for (var right = left + 1; right < indices.Count; right++)
            {
                var apart = Math.Abs(sequence.Frames[indices[right]].TimestampMs - sequence.Frames[indices[left]].TimestampMs);
                Assert.True(apart >= gapMs, $"frames {indices[left]} and {indices[right]} are {apart} ms apart, the gap is {gapMs}");
            }
        }
    }

    /// <summary>The liveness frames include every match frame, are in time order without repeats, and include one inside the moves of the challenge.</summary>
    private static void AssertLivenessFrames(SelectedFrames frames, FrameSequence sequence, SequenceValidationReport report, IdentityDecisionPolicy policy)
    {
        Assert.Equal(frames.LivenessIndices.Order(), frames.LivenessIndices);
        Assert.Equal(frames.LivenessIndices.Count, frames.LivenessIndices.Distinct().Count());
        Assert.All(frames.MatchIndices, index => Assert.Contains(index, frames.LivenessIndices));

        var movesStart = policy.Presence.Challenge.LeadInMs;
        var movesEnd = movesStart + (report.StepPeaks.Count * policy.Presence.Challenge.StepMs);
        Assert.Contains(
            frames.LivenessIndices,
            index => sequence.Frames[index].TimestampMs >= movesStart && sequence.Frames[index].TimestampMs <= movesEnd);
    }

    /// <summary>What the test says of one frame.</summary>
    private sealed class Spec
    {
        public int FaceCount { get; set; } = 1;

        public bool QualityOk { get; set; } = true;

        public double Yaw { get; set; }

        public double Pitch { get; set; }

        public double WidthShare { get; set; } = 0.4;
    }

    /// <summary>A sequence of frames and the report of a validation that passed: what the validation saw in each frame is what the <see cref="Spec"/> says.</summary>
    private sealed class Built(FrameSequence sequence, SequenceValidationReport report) : IDisposable
    {
        public FrameSequence Sequence { get; } = sequence;

        public SequenceValidationReport Report { get; } = report;

        public void Dispose() => Sequence.Dispose();
    }

    /// <summary>
    /// <paramref name="count"/> frames <paramref name="interval"/> milliseconds apart, each the same random gray picture (so every face has the same sharpness and the largest face
    /// ranks first) and the report of a validation of the three moves of the default challenge that passed, with what <paramref name="shape"/> says of each frame.
    /// </summary>
    private static Built Build(int count, Action<int, Spec>? shape = null, int interval = 125)
    {
        var frames = new List<SequenceFrame>(count);
        var observations = new List<FrameObservation>(count);
        for (var index = 0; index < count; index++)
        {
            var spec = new Spec();
            shape?.Invoke(index, spec);

            var gray = TestJpeg.NoisePixels(Width, Height, seed: 11);
            var rgb = new byte[Width * Height * 3];
            for (var pixel = 0; pixel < gray.Length; pixel++)
            {
                rgb[pixel * 3] = gray[pixel];
                rgb[(pixel * 3) + 1] = gray[pixel];
                rgb[(pixel * 3) + 2] = gray[pixel];
            }

            long time = (long)index * interval;
            frames.Add(new SequenceFrame(index, time, new RasterImage(Width, Height, rgb), [0xFF, 0xD8, 0xFF, 0xE0, (byte)index]));

            var face = spec.FaceCount == 0
                ? null
                : SequenceTestFrames.Face(Width, Height, double.IsFinite(spec.Yaw) ? spec.Yaw : 0, double.IsFinite(spec.Pitch) ? spec.Pitch : 0, widthShare: spec.WidthShare);
            var singleFace = spec.FaceCount == 1;
            observations.Add(new FrameObservation(
                index,
                time,
                spec.FaceCount,
                face,
                singleFace ? spec.Yaw : double.NaN,
                singleFace ? spec.Pitch : double.NaN,
                singleFace && spec.QualityOk));
        }

        var sequence = new FrameSequence(frames, SequenceTestFrames.IssuedAt.AddSeconds(30));
        var peaks = Enumerable.Range(0, 3)
            .Select(step => new StepPeak(step, SequenceTestFrames.DefaultSteps[step], 30, 1000 + (step * 1500L)))
            .ToList();
        return new Built(sequence, new SequenceValidationReport(true, [], observations, peaks));
    }
}
