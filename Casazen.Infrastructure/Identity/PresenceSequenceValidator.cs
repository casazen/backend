using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// Validates a sequence of frames as the presence of a person who answers the challenge (E72/F01/US01/T03, NF-192, ADR-013-R17, decisions D6 and D7). The spec: one
/// continuous sequence with one face, enough quality and a coherent answer; a correct move or one promising frame is not enough. Five checks, always in this order, each
/// one collecting its failures (a failure never stops the others, so the guest is told everything at once):
/// <list type="number">
/// <item><description><b>Timing</b> (<c>presence_timing</c>): the frames in the policy's range, the duration within <c>minDurationMs..maxDurationMs</c> (shorter is <c>presence_too_short</c>), the mean rate within half and twice <c>targetFps</c>, no gap above <c>maxGapMs</c> (all correctable), and the duration never longer than the time between the issue of the challenge and the receipt of the sequence: a recording made before the challenge existed is impossible, not correctable.</description></item>
/// <item><description><b>Faces</b> (<c>presence_single_face</c>, correctable): the face detector on every frame, scaled to <c>detectorMaxSide</c>; any frame with two faces or more fails, and the frames without a face may be at most <c>maxShareWithoutFace</c> of the sequence.</description></item>
/// <item><description><b>Quality</b> (<c>presence_quality</c>, correctable): the capture rules of the check-in (light and focus of the face, the face large enough: <c>Biometrics:MinFaceAreaRatio</c>) on the frames with one face; the share of them that fail may be at most <c>maxShareWithoutFace</c> too (the one tolerance the policy has for frames that cannot be used).</description></item>
/// <item><description><b>Continuity</b> (<c>presence_continuity</c>, never correctable): between consecutive frames with one face the centre of the face moves by at most <c>maxCenterJump</c> face widths and its size changes by at most <c>maxSizeChange</c>; the frames are not byte-identical and not all within <c>minPixelDifference</c> grey levels, on average, of the first one (a still picture played again); and the faces of <c>pairSamples</c> frames spread over the sequence are all at least <c>minPairSimilarity</c> alike (a splice of two people fails).</description></item>
/// <item><description><b>Challenge</b> (<c>presence_challenge</c>, <c>presence_challenge_not_followed</c>, correctable): the baseline is the median pose of the lead-in; a step is answered when, inside its window (<c>leadInMs + i x stepMs</c>, with a tolerance of half a step on each side) the pose departs from the baseline in the direction asked by at least <c>turnMinYawDegrees</c> (turns) or <c>lookMinPitchDegrees</c> (looks); the steps are answered in order (each one strictly after the one before) or the check fails.</description></item>
/// </list>
/// Every threshold, count and window is the policy's (the least pixel difference of a moving sequence included); the few constants here are bands the spec fixes (half and
/// twice the rate, half a step) and the geometry of a face (<see cref="FacePose"/>), none a calibration.
/// <para>
/// <b>Never decides.</b> It does not run the liveness or the match (the next slice) and it does not choose the outcome (the closure, E73). <b>Fails closed:</b> with no policy,
/// no models, a model that fails or a time that is over the report is <see cref="SequenceValidationReport.NotAvailable"/>, never <see cref="SequenceValidationReport.Passed"/>;
/// the time is <c>IdentityVerification:Presence:SequenceTimeoutMs</c> and the caller's token, and a caller that cancels gets the cancellation.
/// </para>
/// <para>
/// <b>Privacy</b> (ADR-013-R2, R17): the frames are the caller's, who disposes them; what the validator makes of them (the reduced copies, the gray pictures, the face vectors) is
/// zeroed before it returns, on every path. The log has the counts and the codes of the failures, never a frame, a landmark, an angle or a similarity.
/// </para>
/// </summary>
public sealed class PresenceSequenceValidator(
    IFaceDetector detector,
    IFaceEmbedder embedder,
    IOptions<BiometricModelOptions> biometricOptions,
    IOptions<IdentityVerificationOptions> identityOptions,
    ILogger<PresenceSequenceValidator> logger)
{
    private const double MinRateShare = 0.5;

    private const double MaxRateShare = 2;

    private const double StepToleranceShare = 0.5;

    public Task<SequenceValidationReport> ValidateAsync(FrameSequence sequence, StoredPresenceChallenge challenge, IdentityDecisionPolicy? policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(challenge);
        try
        {
            return Task.FromResult(Validate(sequence, challenge, policy, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<SequenceValidationReport>(ct);
        }
    }

    private SequenceValidationReport Validate(FrameSequence sequence, StoredPresenceChallenge challenge, IdentityDecisionPolicy? policy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var timeoutMs = identityOptions.Value.Presence.SequenceTimeoutMs;
        if (policy is null || timeoutMs <= 0)
        {
            logger.LogWarning("The presence sequence could not be validated: the policy or the time limit is not available; a person decides");
            return SequenceValidationReport.NotAvailable();
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeoutMs);
        try
        {
            return Judge(sequence, challenge, policy, biometricOptions.Value, budget.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("The presence sequence could not be validated in {TimeoutMs} ms; a person decides", timeoutMs);
            return SequenceValidationReport.NotAvailable();
        }
        catch (BiometricUnavailableException)
        {
            logger.LogWarning("The presence sequence could not be validated: the models are not available; a person decides");
            return SequenceValidationReport.NotAvailable();
        }
        catch (Exception ex)
        {
            logger.LogError("The presence sequence could not be validated ({ErrorType}); a person decides", ex.GetType().Name);
            return SequenceValidationReport.NotAvailable();
        }
    }

    private SequenceValidationReport Judge(
        FrameSequence sequence,
        StoredPresenceChallenge challenge,
        IdentityDecisionPolicy policy,
        BiometricModelOptions settings,
        CancellationToken token)
    {
        var rules = policy.Presence;
        var failures = new List<SequenceFailure>();

        CheckTiming(sequence, challenge, rules.Sequence, failures);
        var frames = Observe(sequence, rules.Sequence.DetectorMaxSide, settings, token, out var motion);
        CheckFaces(frames, rules.Sequence, failures);
        CheckQuality(frames, rules.Sequence, failures);
        CheckContinuity(sequence, frames, rules.Continuity, motion, token, failures);
        var peaks = CheckChallenge(frames, challenge, rules, failures);

        logger.LogInformation(
            "Presence sequence validated: passed={Passed}, {Frames} frames, failures=[{Failures}]",
            failures.Count == 0,
            frames.Count,
            string.Join(',', failures.Select(failure => failure.Code ?? failure.Check)));
        return new SequenceValidationReport(failures.Count == 0, failures, frames, peaks);
    }

    private static void CheckTiming(FrameSequence sequence, StoredPresenceChallenge challenge, IdentitySequencePolicy rules, List<SequenceFailure> failures)
    {
        var frames = sequence.Frames;
        var duration = sequence.DurationMs;

        if (duration < rules.MinDurationMs)
            Add(failures, IdentityGuidanceCodes.PresenceTooShort, VerificationReasons.Checks.PresenceTiming, correctable: true);
        else if (duration > rules.MaxDurationMs)
            Add(failures, IdentityGuidanceCodes.PresenceTiming, VerificationReasons.Checks.PresenceTiming, correctable: true);

        if (frames.Count > rules.MaxFrames || (frames.Count < rules.MinFrames && duration >= rules.MinDurationMs))
            Add(failures, IdentityGuidanceCodes.PresenceTiming, VerificationReasons.Checks.PresenceTiming, correctable: true);

        if (frames.Count >= 2 && duration > 0)
        {
            var rate = (frames.Count - 1) * 1000.0 / duration;
            if (rate < rules.TargetFps * MinRateShare || rate > rules.TargetFps * MaxRateShare)
                Add(failures, IdentityGuidanceCodes.PresenceTiming, VerificationReasons.Checks.PresenceTiming, correctable: true);
        }

        long largestGap = 0;
        for (var index = 1; index < frames.Count; index++)
            largestGap = Math.Max(largestGap, frames[index].TimestampMs - frames[index - 1].TimestampMs);

        if (largestGap > rules.MaxGapMs)
            Add(failures, IdentityGuidanceCodes.PresenceTiming, VerificationReasons.Checks.PresenceTiming, correctable: true);

        if (duration > (sequence.ReceivedAt - challenge.IssuedAt).TotalMilliseconds)
            Add(failures, IdentityGuidanceCodes.PresenceTiming, VerificationReasons.Checks.PresenceTiming, correctable: false);
    }

    private sealed record MotionEvidence(bool BytesIdentical, double MaxMeanDifference);

    private List<FrameObservation> Observe(FrameSequence sequence, int detectorMaxSide, BiometricModelOptions settings, CancellationToken token, out MotionEvidence motion)
    {
        var observations = new List<FrameObservation>(sequence.Frames.Count);
        GrayBitmap? first = null;
        var farthest = 0.0;
        try
        {
            foreach (var frame in sequence.Frames)
            {
                token.ThrowIfCancellationRequested();
                observations.Add(ObserveFrame(frame, detectorMaxSide, settings, token, ref first, ref farthest));
            }
        }
        finally
        {
            first?.Dispose();
        }

        motion = new MotionEvidence(BytesIdentical(sequence.Frames), farthest);
        return observations;
    }

    private FrameObservation ObserveFrame(
        SequenceFrame frame,
        int detectorMaxSide,
        BiometricModelOptions settings,
        CancellationToken token,
        ref GrayBitmap? first,
        ref double farthest)
    {
        var image = frame.Image;
        RasterImage? reduced = null;
        GrayBitmap? gray = null;
        try
        {
            var longest = Math.Max(image.Width, image.Height);
            if (longest > detectorMaxSide)
            {
                var scale = (double)detectorMaxSide / longest;
                reduced = image.Resized(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
            }

            var shown = reduced ?? image;
            var faces = detector.Detect(shown, settings.DetectionScoreThreshold, token);

            gray = shown.ToGray();
            Compare(gray, ref first, ref farthest);

            if (faces.Count == 0)
                return new FrameObservation(frame.Index, frame.TimestampMs, 0, null, double.NaN, double.NaN, false);

            var face = ToFramePixels(faces[0], (double)image.Width / shown.Width, (double)image.Height / shown.Height);
            if (faces.Count > 1)
                return new FrameObservation(frame.Index, frame.TimestampMs, faces.Count, face, double.NaN, double.NaN, false);

            var pose = FacePose.Estimate(face);
            var qualityOk = CaptureQualityAssessor.AssessSelfie(image, [face], settings).Acceptable;
            return new FrameObservation(frame.Index, frame.TimestampMs, 1, face, pose.YawDegrees, pose.PitchDegrees, qualityOk);
        }
        finally
        {
            gray?.Dispose();
            reduced?.Dispose();
        }
    }

    private static DetectedFace ToFramePixels(DetectedFace face, double scaleX, double scaleY)
    {
        if (scaleX == 1 && scaleY == 1)
            return face;

        var landmarks = new float[face.Landmarks.Length];
        for (var index = 0; index < landmarks.Length; index++)
            landmarks[index] = (float)(face.Landmarks[index] * (index % 2 == 0 ? scaleX : scaleY));

        return new DetectedFace(
            (float)(face.X * scaleX),
            (float)(face.Y * scaleY),
            (float)(face.Width * scaleX),
            (float)(face.Height * scaleY),
            face.Score,
            landmarks,
            face.AreaRatio);
    }

    private static void Compare(GrayBitmap gray, ref GrayBitmap? first, ref double farthest)
    {
        if (first is null)
        {
            first = new GrayBitmap(gray.Width, gray.Height, (byte[])gray.Pixels.Clone());
            return;
        }

        if (first.Width != gray.Width || first.Height != gray.Height)
        {
            farthest = double.PositiveInfinity;
            return;
        }

        long sum = 0;
        for (var index = 0; index < gray.Pixels.Length; index++)
            sum += Math.Abs(first.Pixels[index] - gray.Pixels[index]);

        farthest = Math.Max(farthest, (double)sum / gray.Pixels.Length);
    }

    private static bool BytesIdentical(IReadOnlyList<SequenceFrame> frames)
    {
        for (var index = 1; index < frames.Count; index++)
        {
            if (!frames[index].Jpeg.AsSpan().SequenceEqual(frames[0].Jpeg))
                return false;
        }

        return frames.Count >= 2;
    }

    private static void CheckFaces(IReadOnlyList<FrameObservation> frames, IdentitySequencePolicy rules, List<SequenceFailure> failures)
    {
        if (frames.Count == 0)
            return;

        var withoutFace = frames.Count(frame => frame.FaceCount == 0);
        if (frames.Any(frame => frame.FaceCount >= 2) || (double)withoutFace / frames.Count > rules.MaxShareWithoutFace)
            Add(failures, IdentityGuidanceCodes.PresenceSingleFace, VerificationReasons.Checks.PresenceSingleFace, correctable: true);
    }

    private static void CheckQuality(IReadOnlyList<FrameObservation> frames, IdentitySequencePolicy rules, List<SequenceFailure> failures)
    {
        var single = frames.Count(frame => frame.FaceCount == 1);
        if (single == 0)
            return;

        var poor = frames.Count(frame => frame.FaceCount == 1 && !frame.QualityOk);
        if ((double)poor / single > rules.MaxShareWithoutFace)
            Add(failures, IdentityGuidanceCodes.PresenceQuality, VerificationReasons.Checks.PresenceQuality, correctable: true);
    }

    private void CheckContinuity(
        FrameSequence sequence,
        IReadOnlyList<FrameObservation> frames,
        IdentityContinuityPolicy rules,
        MotionEvidence motion,
        CancellationToken token,
        List<SequenceFailure> failures)
    {
        var broken = IsStill(frames, motion, rules) || JumpsOrChangesSize(frames, rules);
        var similar = AllFacesAlike(sequence, frames, rules, token);
        if (similar is null)
        {
            broken |= failures.Count == 0;
        }
        else
        {
            broken |= !similar.Value;
        }

        if (broken)
            Add(failures, IdentityGuidanceCodes.PresenceContinuity, VerificationReasons.Checks.PresenceContinuity, correctable: false);
    }

    private static bool IsStill(IReadOnlyList<FrameObservation> frames, MotionEvidence motion, IdentityContinuityPolicy rules)
    {
        if (frames.Count(frame => frame.FaceCount >= 1) < 2)
            return false;

        return motion.BytesIdentical || motion.MaxMeanDifference < rules.MinPixelDifference;
    }

    private static bool JumpsOrChangesSize(IReadOnlyList<FrameObservation> frames, IdentityContinuityPolicy rules)
    {
        for (var index = 1; index < frames.Count; index++)
        {
            if (frames[index - 1] is not { FaceCount: 1, Face: { } before } || frames[index] is not { FaceCount: 1, Face: { } after })
                continue;

            var dx = (after.X + (after.Width / 2.0)) - (before.X + (before.Width / 2.0));
            var dy = (after.Y + (after.Height / 2.0)) - (before.Y + (before.Height / 2.0));
            var faceWidth = (before.Width + after.Width) / 2.0;
            if (Math.Sqrt((dx * dx) + (dy * dy)) > rules.MaxCenterJump * faceWidth)
                return true;

            if (before.Width <= 0 || Math.Abs(after.Width - before.Width) / before.Width > rules.MaxSizeChange)
                return true;
        }

        return false;
    }

    private bool? AllFacesAlike(FrameSequence sequence, IReadOnlyList<FrameObservation> frames, IdentityContinuityPolicy rules, CancellationToken token)
    {
        var usable = new List<int>();
        for (var index = 0; index < frames.Count; index++)
        {
            if (frames[index] is { FaceCount: 1, QualityOk: true, Face: not null })
                usable.Add(index);
        }

        if (usable.Count < 2)
            return null;

        var samples = Math.Clamp(rules.PairSamples, 2, usable.Count);
        var vectors = new List<float[]>(samples);
        try
        {
            for (var sample = 0; sample < samples; sample++)
            {
                token.ThrowIfCancellationRequested();
                var index = usable[(int)Math.Round(sample * (usable.Count - 1) / (double)(samples - 1))];
                vectors.Add(embedder.Embed(sequence.Frames[index].Image, frames[index].Face!, token));
            }

            for (var left = 0; left < vectors.Count; left++)
            {
                for (var right = left + 1; right < vectors.Count; right++)
                {
                    if (Math.Clamp(EmbeddingMath.CosineSimilarity(vectors[left], vectors[right]), 0.0, 1.0) < rules.MinPairSimilarity)
                        return false;
                }
            }

            return true;
        }
        finally
        {
            foreach (var vector in vectors)
                ImageBytes.Zero(vector);
        }
    }

    private static List<StepPeak> CheckChallenge(
        IReadOnlyList<FrameObservation> frames,
        StoredPresenceChallenge challenge,
        IdentityPresencePolicy rules,
        List<SequenceFailure> failures)
    {
        var peaks = new List<StepPeak>(challenge.Steps.Count);
        var poses = frames.Where(frame => frame.FaceCount == 1 && double.IsFinite(frame.YawDegrees) && double.IsFinite(frame.PitchDegrees)).ToList();
        var leadIn = rules.Challenge.LeadInMs;
        var stepMs = rules.Challenge.StepMs;

        var baseline = poses.Where(frame => frame.TimestampMs < leadIn).ToList();
        if (baseline.Count == 0 && poses.Count > 0)
            baseline = [poses[0]];

        var followed = challenge.Steps.Count > 0 && baseline.Count > 0;
        var baseYaw = baseline.Count == 0 ? 0 : Median(baseline.Select(frame => frame.YawDegrees));
        var basePitch = baseline.Count == 0 ? 0 : Median(baseline.Select(frame => frame.PitchDegrees));

        var previousAnswer = long.MinValue;
        for (var step = 0; step < challenge.Steps.Count; step++)
        {
            var action = challenge.Steps[step];
            var minimum = MinimumDeparture(action, rules.Pose);
            var start = leadIn + (step * (double)stepMs) - (stepMs * StepToleranceShare);
            var end = leadIn + ((step + 1) * (double)stepMs) + (stepMs * StepToleranceShare);

            var peak = double.NegativeInfinity;
            long? answeredAt = null;
            foreach (var pose in poses.Where(frame => frame.TimestampMs >= start && frame.TimestampMs <= end))
            {
                var departure = Departure(action, pose, baseYaw, basePitch);
                peak = Math.Max(peak, departure);
                if (answeredAt is null && departure >= minimum)
                    answeredAt = pose.TimestampMs;
            }

            peaks.Add(new StepPeak(step, action, double.IsFinite(peak) ? peak : 0, answeredAt));

            if (answeredAt is null || answeredAt <= previousAnswer)
                followed = false;
            else
                previousAnswer = answeredAt.Value;
        }

        if (!followed)
            Add(failures, IdentityGuidanceCodes.PresenceChallengeNotFollowed, VerificationReasons.Checks.PresenceChallenge, correctable: true);

        return peaks;
    }

    private static double Departure(string action, FrameObservation pose, double baseYaw, double basePitch)
    {
        return action switch
        {
            PresenceActions.TurnLeft => baseYaw - pose.YawDegrees,
            PresenceActions.TurnRight => pose.YawDegrees - baseYaw,
            PresenceActions.LookUp => pose.PitchDegrees - basePitch,
            PresenceActions.LookDown => basePitch - pose.PitchDegrees,
            _ => double.NaN,
        };
    }

    private static double MinimumDeparture(string action, IdentityPosePolicy pose)
    {
        return action switch
        {
            PresenceActions.TurnLeft or PresenceActions.TurnRight => pose.TurnMinYawDegrees,
            PresenceActions.LookUp or PresenceActions.LookDown => pose.LookMinPitchDegrees,
            _ => double.NaN,
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Adds a failure unless the same one (code, check, correctable) is there already.</summary>
    private static void Add(List<SequenceFailure> failures, string code, string check, bool correctable)
    {
        if (!failures.Any(failure => failure.Code == code && failure.Check == check && failure.Correctable == correctable))
            failures.Add(new SequenceFailure(code, check, correctable));
    }
}
