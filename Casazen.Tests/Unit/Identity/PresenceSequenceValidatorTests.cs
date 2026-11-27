using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E72/F01/US01/T03 (NF-192, ADR-013-R17, decisions D6 and D7): the validation of a sequence of frames. A person who answers the challenge in order passes; each defect has
/// its code and the documented flag of whether a new recording can fix it: a recording too short, a timing that is not plausible, two faces, a frame that cannot be judged,
/// a still picture played again, a splice of two people, a recording made before the challenge, a move missing or out of order. The faces, the landmarks and the vectors come from
/// scripts (<see cref="SequenceTestFrames"/>) through the doubles of the face models, frame by frame; every threshold is the committed policy's.
/// </summary>
public class PresenceSequenceValidatorTests
{
    private static readonly IdentityDecisionPolicy Policy = IdentityTestPolicy.Shipped();

    [Fact]
    public async Task ValidateAsync_AGenuineSequenceThatAnswersTheChallengeInOrder_Passes()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);

        var report = await Validator(run).ValidateAsync(run.Sequence, run.Challenge, Policy, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Empty(report.Failures);
        Assert.False(report.IsUnavailable);
        Assert.False(report.AllFailuresCorrectable);

        // Every frame was looked at, once, in order, and shows the one face the script says, good enough to judge.
        Assert.Equal(51, report.Frames.Count);
        Assert.Equal(51, run.Detector.Calls);
        Assert.All(report.Frames, frame =>
        {
            Assert.Equal(1, frame.FaceCount);
            Assert.True(frame.QualityOk);
            Assert.NotNull(frame.Face);
        });
        Assert.Equal(run.Sequence.Frames.Select(frame => frame.TimestampMs), report.Frames.Select(frame => frame.TimestampMs));

        // The three steps, in the order of the challenge, answered one after the other, as far as the script went (1.5 times the least departure of the policy).
        Assert.Equal(run.Challenge.Steps, report.StepPeaks.Select(peak => peak.Action));
        var answered = report.StepPeaks.Select(peak => Assert.NotNull(peak.AnsweredAtMs)).ToList();
        Assert.True(answered[0] < answered[1] && answered[1] < answered[2]);
        Assert.InRange(report.StepPeaks[0].PeakDegrees, 29, 31);
        Assert.InRange(report.StepPeaks[1].PeakDegrees, 17, 19);
        Assert.InRange(report.StepPeaks[2].PeakDegrees, 29, 31);
        Assert.InRange(report.Frames[0].YawDegrees, -0.5, 0.5);
        Assert.InRange(report.Frames[0].PitchDegrees, -0.5, 0.5);

        // The faces of five frames spread over the sequence were compared, and their vectors were zeroed.
        Assert.Equal(5, run.Embedder.Embedded.Count);
        Assert.Equal(0, run.Embedder.Embedded[0]);
        Assert.Equal(50, run.Embedder.Embedded[^1]);
        Assert.Equal(run.Embedder.Embedded.Order(), run.Embedder.Embedded);
        Assert.All(run.Embedder.Issued, vector => Assert.All(vector, value => Assert.Equal(0f, value)));
    }

    [Theory]
    [InlineData("turn_left>look_down>turn_right")]
    [InlineData("look_up>turn_right>look_down")]
    [InlineData("turn_right>look_up>turn_left")]
    [InlineData("look_down>turn_left>look_up")]
    public async Task ValidateAsync_EveryMoveOfTheAlphabet_IsRecognisedInItsOwnDirection(string steps)
    {
        var report = await JudgeAsync(SequenceTestFrames.Genuine(Policy, steps.Split('>')));

        Assert.True(report.Passed);
        Assert.All(report.StepPeaks, peak => Assert.NotNull(peak.AnsweredAtMs));
    }

    [Fact]
    public async Task ValidateAsync_TheBaselineIsThePoseAtRest_NotTheFrontalPose()
    {
        // A guest who rests turned by 12 degrees and looking 6 degrees down answers the same challenge: a move is measured from the lead-in of the same person.
        var report = await JudgeAsync(SequenceTestFrames.Genuine(Policy, baselineYaw: 12, baselinePitch: -6));

        Assert.True(report.Passed);
    }

    [Fact]
    public async Task ValidateAsync_TheMovesNeedTheDepartureOfThePolicy_AndNotLess()
    {
        var enough = await JudgeAsync(SequenceTestFrames.Genuine(Policy, amplitude: 1.1));
        var tooLittle = await JudgeAsync(SequenceTestFrames.Genuine(Policy, amplitude: 0.9));

        Assert.True(enough.Passed);
        Assert.False(tooLittle.Passed);
        Assert.All(tooLittle.StepPeaks, peak => Assert.Null(peak.AnsweredAtMs));
        Assert.InRange(tooLittle.StepPeaks[0].PeakDegrees, 17, 19);
        Assert.InRange(tooLittle.StepPeaks[1].PeakDegrees, 10, 11.5);
        var failure = Assert.Single(tooLittle.Failures);
        Assert.Equal((IdentityGuidanceCodes.PresenceChallengeNotFollowed, VerificationReasons.Checks.PresenceChallenge, true), (failure.Code, failure.Check, failure.Correctable));
    }

    [Fact]
    public async Task ValidateAsync_TheDepartureNeededIsTheOneOfTheActiveConfiguration()
    {
        // The least departure is the policy's: the same 22 degrees that answer a turn of 20 do not answer a turn of 25.
        var stricter = Policy with { Presence = Policy.Presence with { Pose = Policy.Presence.Pose with { TurnMinYawDegrees = 25 } } };
        var script = SequenceTestFrames.Genuine(Policy, amplitude: 1.1);

        var report = await JudgeAsync(script, stricter);

        Assert.False(report.Passed);
        Assert.Null(report.StepPeaks[0].AnsweredAtMs);
        Assert.NotNull(report.StepPeaks[1].AnsweredAtMs);
    }

    [Fact]
    public async Task PresenceSequenceValidator_StaticReplay_FailsContinuity()
    {
        // Security (NF-192, AC-NF192-2): a picture played again, with the face where it was and no move, is never a presence: not with the same bytes in every frame, not with
        // a grey level of noise either; and no new recording explains it.
        foreach (var noise in new[] { 0, 1 })
        {
            var report = await JudgeAsync(SequenceTestFrames.StaticReplay(Policy, noise));

            Assert.False(report.Passed);
            var still = Assert.Single(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceContinuity);
            Assert.Equal((VerificationReasons.Checks.PresenceContinuity, false), (still.Check, still.Correctable));
            Assert.Contains(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceChallengeNotFollowed && failure.Correctable);
            Assert.False(report.AllFailuresCorrectable);
        }
    }

    [Fact]
    public async Task ValidateAsync_AMovingHead_IsNotAStillPicture_EvenWithTheSamePixelsOutsideTheFace()
    {
        // The pixels of the frames differ (the script draws noise for each), so a sequence that answers the challenge is not called still.
        var report = await JudgeAsync(SequenceTestFrames.Genuine(Policy));

        Assert.DoesNotContain(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceContinuity);
    }

    [Fact]
    public async Task PresenceSequenceValidator_LongerThanSinceIssue_Fails()
    {
        // Security (NF-192, AC-NF192-3): a recording that lasted longer than the time between the challenge and its receipt was made before the challenge existed.
        var script = SequenceTestFrames.PreChallenge(Policy);

        var report = await JudgeAsync(script);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Equal((IdentityGuidanceCodes.PresenceTiming, VerificationReasons.Checks.PresenceTiming, false), (failure.Code, failure.Check, failure.Correctable));
        Assert.False(report.AllFailuresCorrectable);

        // The limit is exact: a recording as long as the time since the challenge is possible, one millisecond longer is not.
        script.ReceivedAfterIssue = TimeSpan.FromMilliseconds(script.DurationMs);
        Assert.True((await JudgeAsync(script)).Passed);
        script.ReceivedAfterIssue = TimeSpan.FromMilliseconds(script.DurationMs - 1);
        Assert.False((await JudgeAsync(script)).Passed);
    }

    [Fact]
    public async Task ValidateAsync_ARecordingThatIsTooShort_IsCorrectable_AndSaysItOnce()
    {
        var report = await JudgeAsync(SequenceTestFrames.TooShort(Policy));

        Assert.False(report.Passed);
        var tooShort = Assert.Single(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceTooShort);
        Assert.Equal((VerificationReasons.Checks.PresenceTiming, true), (tooShort.Check, tooShort.Correctable));
        Assert.DoesNotContain(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceTiming);
        Assert.True(report.AllFailuresCorrectable);
    }

    [Theory]
    [InlineData("too long")]
    [InlineData("too few frames")]
    [InlineData("too many frames")]
    [InlineData("too fast")]
    [InlineData("a gap")]
    public async Task ValidateAsync_ATimingThatIsNotPlausible_IsCorrectable(string defect)
    {
        var policy = Policy;
        var script = SequenceTestFrames.Genuine(Policy);
        switch (defect)
        {
            case "too long":
                policy = Policy with { Presence = Policy.Presence with { Sequence = Policy.Presence.Sequence with { MaxDurationMs = 5000 } } };
                break;
            case "too few frames":
                // One frame in three, which is a rate of 2.7 a second: under half of the 8 the page captures at, and fewer frames than the policy asks for.
                script.Frames.RemoveAll(frame => frame.TimestampMs / 125 % 3 != 0);
                break;
            case "too many frames":
                policy = Policy with { Presence = Policy.Presence with { Sequence = Policy.Presence.Sequence with { MaxFrames = 40 } } };
                break;
            case "too fast":
                // 20 frames a second against a target of 8: more than twice.
                var fast = Policy with { Presence = Policy.Presence with { Sequence = Policy.Presence.Sequence with { TargetFps = 20, MaxFrames = 300 } } };
                script = SequenceTestFrames.Genuine(fast);
                policy = Policy with { Presence = Policy.Presence with { Sequence = Policy.Presence.Sequence with { MaxFrames = 300 } } };
                break;
            case "a gap":
                script.Frames.RemoveRange(20, 5);
                break;
        }

        var report = await JudgeAsync(script, policy);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures, entry => entry.Code == IdentityGuidanceCodes.PresenceTiming);
        Assert.Equal((VerificationReasons.Checks.PresenceTiming, true), (failure.Check, failure.Correctable));
    }

    [Fact]
    public async Task ValidateAsync_TwoFacesInAFrame_AreCorrectable_AndTheFrameHasNoPose()
    {
        var report = await JudgeAsync(SequenceTestFrames.TwoFaces(Policy));

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Equal((IdentityGuidanceCodes.PresenceSingleFace, VerificationReasons.Checks.PresenceSingleFace, true), (failure.Code, failure.Check, failure.Correctable));
        Assert.True(report.AllFailuresCorrectable);
        Assert.Equal(2, report.Frames[10].FaceCount);
        Assert.NotNull(report.Frames[10].Face);
        Assert.True(double.IsNaN(report.Frames[10].YawDegrees));
        Assert.False(report.Frames[10].QualityOk);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task ValidateAsync_FramesWithoutAFace_AreToleratedUpToTheShareOfThePolicy(int frames, bool passes)
    {
        // The policy tolerates a tenth of the frames: 5 of 51 is 9.8 per cent, 6 is 11.8.
        var script = SequenceTestFrames.Genuine(Policy);
        foreach (var frame in script.Frames.TakeLast(frames))
            frame.Faces = [];

        var report = await JudgeAsync(script);

        Assert.Equal(passes, report.Passed);
        Assert.Equal(frames, report.Frames.Count(frame => frame.FaceCount == 0));
        Assert.All(report.Frames.Where(frame => frame.FaceCount == 0), frame =>
        {
            Assert.Null(frame.Face);
            Assert.False(frame.QualityOk);
        });
        if (!passes)
        {
            var failure = Assert.Single(report.Failures);
            Assert.Equal((IdentityGuidanceCodes.PresenceSingleFace, true), (failure.Code, failure.Correctable));
        }
    }

    [Theory]
    [InlineData("dark", 5, true)]
    [InlineData("dark", 6, false)]
    [InlineData("out of focus", 5, true)]
    [InlineData("out of focus", 6, false)]
    public async Task ValidateAsync_FramesThatCannotBeJudged_AreToleratedUpToTheShareOfThePolicy(string defect, int frames, bool passes)
    {
        var script = SequenceTestFrames.Genuine(Policy);
        foreach (var frame in script.Frames.TakeLast(frames))
        {
            // Too dark: a tenth of the light. Out of focus: one flat gray, no edge at all (the blur that is a picture of nothing).
            frame.Gray = defect == "dark" ? [.. frame.Gray.Select(value => (byte)(value / 10))] : [.. frame.Gray.Select(_ => (byte)128)];
        }

        var report = await JudgeAsync(script);

        Assert.Equal(passes, report.Passed);
        Assert.Equal(frames, report.Frames.Count(frame => !frame.QualityOk));
        if (!passes)
        {
            var failure = Assert.Single(report.Failures);
            Assert.Equal((IdentityGuidanceCodes.PresenceQuality, VerificationReasons.Checks.PresenceQuality, true), (failure.Code, failure.Check, failure.Correctable));
        }
    }

    [Fact]
    public async Task ValidateAsync_AFaceTooSmallInTheFrame_IsAQualityFailure_NotAMissingFace()
    {
        var script = SequenceTestFrames.Genuine(Policy);
        foreach (var frame in script.Frames)
            frame.Faces = [SequenceTestFrames.Face(script.Width, script.Height, widthShare: 0.2)];

        var report = await JudgeAsync(script);

        Assert.False(report.Passed);
        Assert.All(report.Frames, frame => Assert.Equal(1, frame.FaceCount));
        Assert.Contains(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceQuality && failure.Correctable);
        Assert.DoesNotContain(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceSingleFace);
    }

    [Fact]
    public async Task ValidateAsync_ASpliceOfTwoPeople_FailsContinuity_AndNoRetryExplainsIt()
    {
        using var run = SequenceTestFrames.Splice(Policy).Run(Policy);

        var report = await Validator(run).ValidateAsync(run.Sequence, run.Challenge, Policy, CancellationToken.None);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Equal((IdentityGuidanceCodes.PresenceContinuity, VerificationReasons.Checks.PresenceContinuity, false), (failure.Code, failure.Check, failure.Correctable));
        Assert.False(report.AllFailuresCorrectable);

        // The frames compared were spread over the sequence, from both halves, and their vectors were zeroed.
        Assert.Contains(run.Embedder.Embedded, index => index < 25);
        Assert.Contains(run.Embedder.Embedded, index => index > 25);
        Assert.All(run.Embedder.Issued, vector => Assert.All(vector, value => Assert.Equal(0f, value)));
    }

    [Fact]
    public async Task ValidateAsync_TheSimilarityBetweenTheFramesIsTheOneOfThePolicy()
    {
        var script = SequenceTestFrames.Genuine(Policy);
        foreach (var frame in script.Frames.Skip(25))
            frame.Person = [0.8f, 0.6f, 0, 0, 0, 0, 0, 0];

        // A cosine of 0.8 between the halves: above the 0.5 of the policy, under 0.9.
        var alike = await JudgeAsync(script);
        var stricter = Policy with { Presence = Policy.Presence with { Continuity = Policy.Presence.Continuity with { MinPairSimilarity = 0.9 } } };
        var unlike = await JudgeAsync(script, stricter);

        Assert.True(alike.Passed);
        Assert.False(unlike.Passed);
        Assert.Contains(unlike.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceContinuity && !failure.Correctable);
    }

    [Fact]
    public async Task ValidateAsync_TheFacesComparedAreAsManyAsThePolicySays_AndNeverMoreThanTheFramesThatCanBeJudged()
    {
        var script = SequenceTestFrames.Genuine(Policy);
        var three = Policy with { Presence = Policy.Presence with { Continuity = Policy.Presence.Continuity with { PairSamples = 3 } } };
        using var run = script.Run(three);

        await Validator(run).ValidateAsync(run.Sequence, run.Challenge, three, CancellationToken.None);

        Assert.Equal(3, run.Embedder.Embedded.Count);
    }

    [Theory]
    [InlineData("jump")]
    [InlineData("size")]
    public async Task ValidateAsync_AFaceThatJumpsOrChangesSize_FailsContinuity(string defect)
    {
        var script = SequenceTestFrames.Genuine(Policy);
        script.Frames[30].Faces = defect == "jump"
            ? [SequenceTestFrames.Face(script.Width, script.Height, centerX: 0.75)]
            : [SequenceTestFrames.Face(script.Width, script.Height, widthShare: 0.3)];

        var report = await JudgeAsync(script);

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Equal((IdentityGuidanceCodes.PresenceContinuity, false), (failure.Code, failure.Correctable));
    }

    [Fact]
    public async Task ValidateAsync_AFaceThatMovesALittle_IsContinuous()
    {
        // The centre moves by 0.04 face widths and the size by 5 per cent from one frame to the next: well within the policy.
        var script = SequenceTestFrames.Genuine(Policy);
        for (var index = 0; index < script.Frames.Count; index++)
        {
            var odd = index % 2;
            script.Frames[index].Faces = [SequenceTestFrames.Face(script.Width, script.Height, centerX: 0.5 + (0.02 * odd), widthShare: 0.5 * (1 + (0.05 * odd)))];
        }

        var report = await JudgeAsync(script);

        Assert.DoesNotContain(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceContinuity);
    }

    [Fact]
    public async Task ValidateAsync_MovesOutOfOrder_FailTheChallenge_AndAreCorrectable()
    {
        var report = await JudgeAsync(SequenceTestFrames.OutOfOrder(Policy));

        Assert.False(report.Passed);
        var failure = Assert.Single(report.Failures);
        Assert.Equal((IdentityGuidanceCodes.PresenceChallengeNotFollowed, VerificationReasons.Checks.PresenceChallenge, true), (failure.Code, failure.Check, failure.Correctable));
        Assert.True(report.AllFailuresCorrectable);

        // Both moves were made, so both are answered, but the look came before the turn it should follow.
        Assert.All(report.StepPeaks, peak => Assert.NotNull(peak.AnsweredAtMs));
        Assert.True(report.StepPeaks[1].AnsweredAtMs < report.StepPeaks[0].AnsweredAtMs);
    }

    [Fact]
    public async Task ValidateAsync_AMissingMove_FailsTheChallenge()
    {
        var script = SequenceTestFrames.Genuine(Policy, performed: [PresenceActions.TurnLeft, PresenceActions.LookUp]);

        var report = await JudgeAsync(script);

        Assert.False(report.Passed);
        Assert.NotNull(report.StepPeaks[0].AnsweredAtMs);
        Assert.NotNull(report.StepPeaks[1].AnsweredAtMs);
        Assert.Null(report.StepPeaks[2].AnsweredAtMs);
        Assert.Contains(report.Failures, failure => failure.Code == IdentityGuidanceCodes.PresenceChallengeNotFollowed && failure.Correctable);
    }

    [Fact]
    public async Task ValidateAsync_AMoveTheOtherWay_IsNotTheMoveAsked()
    {
        var script = SequenceTestFrames.Genuine(Policy, performed: [PresenceActions.TurnRight, PresenceActions.LookUp, PresenceActions.TurnRight]);

        var report = await JudgeAsync(script);

        Assert.False(report.Passed);
        Assert.Null(report.StepPeaks[0].AnsweredAtMs);
        Assert.InRange(report.StepPeaks[0].PeakDegrees, -1, 0.5);
    }

    [Fact]
    public async Task ValidateAsync_EveryFailureIsCollected_InTheOrderOfTheChecks()
    {
        var script = SequenceTestFrames.OutOfOrder(Policy);
        for (var index = 10; index < 15; index++)
            script.Frames[index].Faces.Add(SequenceTestFrames.Face(script.Width, script.Height, centerX: 0.18, centerY: 0.55, widthShare: 0.2));

        script.ReceivedAfterIssue = TimeSpan.FromMilliseconds(script.DurationMs - 1000);

        var report = await JudgeAsync(script);

        Assert.Equal(
            [IdentityGuidanceCodes.PresenceTiming, IdentityGuidanceCodes.PresenceSingleFace, IdentityGuidanceCodes.PresenceChallengeNotFollowed],
            report.Failures.Select(failure => failure.Code));
        Assert.Equal([false, true, true], report.Failures.Select(failure => failure.Correctable));
        Assert.False(report.AllFailuresCorrectable);
    }

    [Fact]
    public async Task ValidateAsync_WhenTheTimeIsOver_IsNotAvailable_NeverPassed()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        run.Detector.WaitForCancellation = true;

        var report = await Validator(run, timeoutMs: 50).ValidateAsync(run.Sequence, run.Challenge, Policy, CancellationToken.None);

        AssertNotAvailable(report);
    }

    [Fact]
    public async Task ValidateAsync_WhenTheCallerCancels_IsTheCancellation_NotAReport()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        run.Detector.WaitForCancellation = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Validator(run).ValidateAsync(run.Sequence, run.Challenge, Policy, cancellation.Token));

        using var other = SequenceTestFrames.Genuine(Policy).Run(Policy);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Validator(other).ValidateAsync(other.Sequence, other.Challenge, Policy, new CancellationToken(canceled: true)));
        Assert.Equal(0, other.Detector.Calls);
    }

    [Theory]
    [InlineData("models not available")]
    [InlineData("detector fails")]
    [InlineData("embedder fails")]
    public async Task ValidateAsync_WhenAModelIsNotAvailableOrFails_IsNotAvailable_AndTheMessageIsNotLogged(string failure)
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);
        var logger = new CapturingLogger<PresenceSequenceValidator>();
        switch (failure)
        {
            case "models not available":
                run.Detector.Failure = new BiometricUnavailableException();
                break;
            case "detector fails":
                run.Detector.Failure = new InvalidOperationException("secret text of the detector");
                break;
            default:
                run.Embedder.Failure = new InvalidDataException("secret text of the embedder");
                break;
        }

        var report = await Validator(run, logger: logger).ValidateAsync(run.Sequence, run.Challenge, Policy, CancellationToken.None);

        AssertNotAvailable(report);
        Assert.DoesNotContain("secret", logger.AllOutput, StringComparison.Ordinal);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task ValidateAsync_WithoutAPolicy_IsNotAvailable_AndNothingIsDetected()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);

        var report = await Validator(run).ValidateAsync(run.Sequence, run.Challenge, policy: null, CancellationToken.None);

        AssertNotAvailable(report);
        Assert.Equal(0, run.Detector.Calls);
    }

    [Fact]
    public async Task ValidateAsync_WithoutATimeLimit_IsNotAvailable()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);

        var report = await Validator(run, timeoutMs: 0).ValidateAsync(run.Sequence, run.Challenge, Policy, CancellationToken.None);

        AssertNotAvailable(report);
        Assert.Equal(0, run.Detector.Calls);
    }

    [Fact]
    public async Task ValidateAsync_FramesLargerThanTheDetectorSide_AreReducedForTheDetector_AndTheReducedCopiesAreZeroed()
    {
        // 640 x 480 frames, a detector side of 320: the detector is shown 320 x 240 copies, the faces come back in the pixels of the frame as it was received, and the
        // copies are zeroed while the frames stay the caller's.
        var script = SequenceTestFrames.Genuine(Policy, width: 640, height: 480);
        using var run = script.Run(Policy);

        var report = await Validator(run).ValidateAsync(run.Sequence, run.Challenge, Policy, CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal(51, run.Detector.SeenPixels.Count);
        Assert.All(run.Detector.SeenPixels, pixels =>
        {
            Assert.Equal(320 * 240 * 3, pixels.Length);
            Assert.All(pixels, value => Assert.Equal(0, value));
        });
        Assert.All(run.Sequence.Frames, frame => Assert.Contains(frame.Image.Rgb, value => value != 0));
        var expected = script.Frames[10].Faces[0];
        var reported = report.Frames[10].Face!;
        Assert.Equal(expected.X, reported.X, 0.5f);
        Assert.Equal(expected.Width, reported.Width, 0.5f);
        Assert.Equal(expected.Landmarks[4], reported.Landmarks[4], 0.5f);
        Assert.Equal(expected.Landmarks[5], reported.Landmarks[5], 0.5f);
    }

    [Fact]
    public async Task ValidateAsync_FramesWithinTheDetectorSide_AreShownAsTheyAre_AndNotZeroed()
    {
        using var run = SequenceTestFrames.Genuine(Policy).Run(Policy);

        await Validator(run).ValidateAsync(run.Sequence, run.Challenge, Policy, CancellationToken.None);

        Assert.Equal(run.Sequence.Frames.Count, run.Detector.SeenPixels.Count);
        for (var index = 0; index < run.Sequence.Frames.Count; index++)
            Assert.Same(run.Sequence.Frames[index].Image.Rgb, run.Detector.SeenPixels[index]);

        Assert.All(run.Detector.SeenPixels, pixels => Assert.Contains(pixels, value => value != 0));
    }

    [Fact]
    public async Task ValidateAsync_TheLog_HasTheCountsAndTheCodesOfTheFailures_Only()
    {
        var logger = new CapturingLogger<PresenceSequenceValidator>();
        using var genuine = SequenceTestFrames.Genuine(Policy).Run(Policy);
        using var outOfOrder = SequenceTestFrames.OutOfOrder(Policy).Run(Policy);

        await Validator(genuine, logger: logger).ValidateAsync(genuine.Sequence, genuine.Challenge, Policy, CancellationToken.None);
        await Validator(outOfOrder, logger: logger).ValidateAsync(outOfOrder.Sequence, outOfOrder.Challenge, Policy, CancellationToken.None);

        Assert.Equal(
            [
                "Presence sequence validated: passed=True, 51 frames, failures=[]",
                "Presence sequence validated: passed=False, 51 frames, failures=[presence_challenge_not_followed]",
            ],
            logger.Entries.Select(entry => entry.Message));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    private static PresenceSequenceValidator Validator(ScriptedRun run, int timeoutMs = 8000, CapturingLogger<PresenceSequenceValidator>? logger = null)
    {
        var options = new IdentityVerificationOptions { Presence = { SequenceTimeoutMs = timeoutMs } };
        return new PresenceSequenceValidator(
            run.Detector,
            run.Embedder,
            Options.Create(new BiometricModelOptions()),
            Options.Create(options),
            logger ?? new CapturingLogger<PresenceSequenceValidator>());
    }

    private static async Task<SequenceValidationReport> JudgeAsync(ScriptedSequence script, IdentityDecisionPolicy? policy = null)
    {
        using var run = script.Run(Policy);
        return await Validator(run).ValidateAsync(run.Sequence, run.Challenge, policy ?? Policy, CancellationToken.None);
    }

    /// <summary>A sequence that could not be judged: never passed, the one failure of the check <c>processing</c>, nothing asked of the guest, nothing else in the report.</summary>
    private static void AssertNotAvailable(SequenceValidationReport report)
    {
        Assert.False(report.Passed);
        Assert.True(report.IsUnavailable);
        Assert.False(report.AllFailuresCorrectable);
        var failure = Assert.Single(report.Failures);
        Assert.Equal((null, VerificationReasons.Checks.Processing, false, VerificationReasons.Results.Unavailable), (failure.Code, failure.Check, failure.Correctable, failure.Result));
        Assert.Empty(report.Frames);
        Assert.Empty(report.StepPeaks);
    }
}
