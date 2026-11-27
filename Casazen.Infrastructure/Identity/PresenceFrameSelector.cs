using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The frames the server chose from a validated sequence (E72/F01/US02/T01, NF-192, NF-193, ADR-013-R17, decision D6): the ones the portrait of the document is compared
/// with and the ones the anti-spoofing model judges. Places in <see cref="FrameSequence.Frames"/>, in time order, never pixels, never a number of a person.
/// </summary>
/// <param name="MatchIndices">
/// The frames the match compares with the portrait: <c>match.frames</c> of them, each with one face, good quality and a near-frontal pose, at least
/// <c>match.minFrameGapMs</c> apart, drawn at random among the best candidates.
/// </param>
/// <param name="LivenessIndices">
/// The frames the liveness is judged on: every frame of <paramref name="MatchIndices"/> (a frame that is compared is also judged), frames spread over the whole
/// sequence, and at least one inside the moves of the challenge; <c>presence.liveness.frames</c> of them, or more only where the two rules above need it.
/// </param>
public sealed record SelectedFrames(IReadOnlyList<int> MatchIndices, IReadOnlyList<int> LivenessIndices);

/// <summary>
/// What <see cref="PresenceFrameSelector.Select"/> gives: the frames, or why they could not be chosen. A selection that could not be made is the one failure of the check
/// <c>presence_quality</c>, correctable (a new recording in better light and nearer the camera can fix it), with the guidance <c>presence_quality</c>.
/// </summary>
/// <param name="Frames">The frames; null when there were not enough usable ones.</param>
/// <param name="Failure">Why not, a failure of the check <c>presence_quality</c>; null when the frames were chosen.</param>
public sealed record FrameSelection(SelectedFrames? Frames, SequenceFailure? Failure)
{
    /// <summary>True when the server chose the frames: then <see cref="Frames"/> is set and <see cref="Failure"/> is null.</summary>
    [MemberNotNullWhen(true, nameof(Frames))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSelected => Frames is not null;

    internal static FrameSelection Chosen(SelectedFrames frames)
    {
        return new FrameSelection(frames, null);
    }

    /// <summary>Not enough frames with one face, in good quality and near-frontal (for the match), or usable at all (for the liveness): <c>presence_quality</c>, correctable.</summary>
    internal static FrameSelection NotEnoughFrames()
    {
        return new FrameSelection(
            null,
            new SequenceFailure(IdentityGuidanceCodes.PresenceQuality, VerificationReasons.Checks.PresenceQuality, Correctable: true));
    }
}

/// <summary>
/// The server chooses the frames of a validated presence sequence that are compared with the portrait of the document and judged by the anti-spoofing model (E72/F01/US02/T01,
/// NF-192, NF-193, ADR-013-R17, decisions D6 and D7): the spec says the comparison is made on «frames selected by the server from the same validated sequence», so the client
/// can never choose which of its frames decide. The page sends a dense sequence and learns nothing of the choice.
/// <list type="number">
/// <item><description><b>Match frames.</b> The candidates are the frames of the sequence with exactly one face, good quality (the capture rules the validation applied) and a near-frontal
/// pose (the yaw and the pitch of the head within <c>presence.pose.neutralMaxDegrees</c>), ranked by the area of the face times its sharpness. <c>match.frames</c> of them are
/// picked, one after the other, each at least <c>match.minFrameGapMs</c> after or before every earlier pick; at each pick the choice is drawn with
/// <see cref="RandomNumberGenerator"/> among the best <c>match.randomizeAmongTop</c> that remain. A candidate is only offered to the draw when the picks still to make can
/// be made after it (the farthest the gap allows, found greedily in time order), so the draw never walks into a dead end that another choice would have avoided: the
/// selection fails only when no set of <c>match.frames</c> frames respects the gap at all.</description></item>
/// <item><description><b>Liveness frames.</b> The match frames, first (every frame that is compared is also judged); then, if none of them is inside the moves of the challenge (from
/// <c>presence.challenge.leadInMs</c> to the end of its last step), one frame that is; then frames spread over the whole sequence (each pick is drawn among the
/// <c>match.randomizeAmongTop</c> frames farthest in time from the ones already chosen) up to <c>presence.liveness.frames</c>. When the match frames alone are more than that
/// number, or one more is needed for a frame inside the moves, the set is larger than the policy says, never smaller: a frame that is compared is always judged.</description></item>
/// </list>
/// <b>Never on a report that did not pass.</b> A report that did not pass, or that could not be judged, or that is not the report of this sequence, is a mistake of the
/// caller: <see cref="InvalidOperationException"/>, never a selection. <b>Not enough frames</b> (not as many near-frontal candidates as <c>match.frames</c>, no usable frame
/// inside the moves, fewer usable frames than the liveness needs) is the failure <c>presence_quality</c> of <see cref="FrameSelection"/>, correctable.
/// <para>
/// <b>Privacy.</b> It works on what the validation saw and on the pixels of the frames in memory only; the gray copies it makes of the faces are zeroed before it returns.
/// It has no logger: the ranking numbers never leave it, and what it returns is places in the sequence. Every threshold, count and window is the policy's; the few
/// constants here are the geometry of the ranking (area times sharpness) fixed by the spec, none a calibration.
/// </para>
/// </summary>
public sealed class PresenceFrameSelector
{
    private readonly Func<int, int> _draw;

    /// <summary>The selector of the application: every draw is made with <see cref="RandomNumberGenerator"/>.</summary>
    public PresenceFrameSelector()
        : this(RandomNumberGenerator.GetInt32)
    {
    }

    /// <summary>The selector with the draw the tests choose (a value from 0 up to, not including, the number it is given).</summary>
    internal PresenceFrameSelector(Func<int, int> draw)
    {
        _draw = draw;
    }

    /// <summary>
    /// Chooses the frames of <paramref name="sequence"/>, whose validation gave <paramref name="report"/> (which must have passed), with <paramref name="policy"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The report did not pass, or it is not the report of <paramref name="sequence"/>.</exception>
    public FrameSelection Select(FrameSequence sequence, SequenceValidationReport report, IdentityDecisionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(policy);
        RequireValidatedSequence(sequence, report);

        var candidates = NearFrontalCandidates(sequence, report.Frames, policy.Presence.Pose);
        var match = PickMatchFrames(candidates, policy.Match);
        if (match is null)
            return FrameSelection.NotEnoughFrames();

        var liveness = PickLivenessFrames(report, match, policy);
        if (liveness is null)
            return FrameSelection.NotEnoughFrames();

        return FrameSelection.Chosen(new SelectedFrames([.. match.Select(candidate => candidate.Index).Order()], liveness));
    }

    /// <summary>The guard: the report passed, and it is the report of this very sequence (the same frames at the same times).</summary>
    private static void RequireValidatedSequence(FrameSequence sequence, SequenceValidationReport report)
    {
        // A report that could not be judged is never Passed (SequenceValidationReport.NotAvailable); a "passed" report that lists a failure is not one either.
        if (!report.Passed || report.Failures.Count > 0)
            throw new InvalidOperationException("Frames are chosen only from a sequence that passed its validation.");

        var frames = sequence.Frames;
        if (report.Frames.Count != frames.Count)
            throw new InvalidOperationException("The validation report is not the one of this sequence.");

        for (var index = 0; index < frames.Count; index++)
        {
            if (report.Frames[index].Index != frames[index].Index || report.Frames[index].TimestampMs != frames[index].TimestampMs)
                throw new InvalidOperationException("The validation report is not the one of this sequence.");
        }
    }

    /// <summary>One frame that can be chosen: its place and time in the sequence, and its rank (the face area times its sharpness; 0 for a frame that is not ranked).</summary>
    private readonly record struct FrameCandidate(int Index, long TimestampMs, double Rank);

    /// <summary>The frames with exactly one face, good quality and a pose within <c>neutralMaxDegrees</c> of the front on both axes, each with its rank.</summary>
    private static List<FrameCandidate> NearFrontalCandidates(FrameSequence sequence, IReadOnlyList<FrameObservation> observations, IdentityPosePolicy pose)
    {
        var candidates = new List<FrameCandidate>();
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            if (observation is not { FaceCount: 1, QualityOk: true, Face: { } face })
                continue;

            // NaN (no estimate of the pose) is not near the front; the comparison is false for it, so it is refused.
            var nearFront = Math.Abs(observation.YawDegrees) <= pose.NeutralMaxDegrees && Math.Abs(observation.PitchDegrees) <= pose.NeutralMaxDegrees;
            if (!nearFront)
                continue;

            candidates.Add(new FrameCandidate(index, observation.TimestampMs, Rank(sequence.Frames[index].Image, face)));
        }

        return candidates;
    }

    /// <summary>
    /// The area of the face (the box in the pixels of the frame) times its sharpness: one minus the share of the edge energy of the face that survives a blur, the no-reference
    /// measure the capture rules use. The gray copy is zeroed before this returns.
    /// </summary>
    private static double Rank(RasterImage image, DetectedFace face)
    {
        using var region = CaptureQualityAssessor.Statistics(image, face);
        if (region is null)
            return 0;

        var sharpness = 1.0 - MrzImageLocator.BlurRatio(region);
        var rank = (double)face.Width * face.Height * sharpness;
        return double.IsFinite(rank) ? Math.Max(0, rank) : 0;
    }

    // ───── the match frames ─────

    private List<FrameCandidate>? PickMatchFrames(List<FrameCandidate> candidates, IdentityMatchPolicy rules)
    {
        var ranked = candidates.OrderByDescending(candidate => candidate.Rank).ThenBy(candidate => candidate.Index).ToList();
        var picked = new List<FrameCandidate>(rules.Frames);
        while (picked.Count < rules.Frames)
        {
            // Respecting the gap with every earlier pick.
            var eligible = ranked
                .Where(candidate => picked.All(earlier => earlier.Index != candidate.Index && Math.Abs(candidate.TimestampMs - earlier.TimestampMs) >= rules.MinFrameGapMs))
                .ToList();

            // The best ones that do not make the rest of the picks impossible.
            var best = new List<FrameCandidate>(rules.RandomizeAmongTop);
            foreach (var candidate in eligible)
            {
                if (CanComplete(candidate, eligible, rules.Frames - picked.Count - 1, rules.MinFrameGapMs))
                    best.Add(candidate);

                if (best.Count == rules.RandomizeAmongTop)
                    break;
            }

            if (best.Count == 0)
                return null;

            picked.Add(best[_draw(best.Count)]);
        }

        return picked;
    }

    /// <summary>
    /// Whether <paramref name="needed"/> more frames can still be picked after <paramref name="chosen"/>: among the eligible ones at least the gap from it, the most that are at
    /// least the gap from each other is found by taking them in time order and keeping each that is far enough from the last one kept.
    /// </summary>
    private static bool CanComplete(FrameCandidate chosen, List<FrameCandidate> eligible, int needed, int gap)
    {
        if (needed <= 0)
            return true;

        var count = 0;
        long last = 0;
        foreach (var candidate in eligible.Where(other => other.Index != chosen.Index && Math.Abs(other.TimestampMs - chosen.TimestampMs) >= gap).OrderBy(other => other.TimestampMs))
        {
            if (count > 0 && candidate.TimestampMs - last < gap)
                continue;

            count++;
            last = candidate.TimestampMs;
            if (count >= needed)
                return true;
        }

        return false;
    }

    // ───── the liveness frames ─────

    private int[]? PickLivenessFrames(SequenceValidationReport report, List<FrameCandidate> match, IdentityDecisionPolicy policy)
    {
        var chosen = new List<FrameCandidate>(match);
        var target = Math.Max(policy.Presence.Liveness.Frames, match.Count);
        var top = policy.Match.RandomizeAmongTop;

        // The moves of the challenge: from the end of the lead-in to the end of the last step (the windows of the steps follow each other).
        var steps = report.StepPeaks.Count > 0 ? report.StepPeaks.Count : policy.Presence.Challenge.Steps;
        var movesStart = (long)policy.Presence.Challenge.LeadInMs;
        var movesEnd = movesStart + (steps * (long)policy.Presence.Challenge.StepMs);

        // Every frame that can be judged and is not chosen yet: one face, good quality, whatever the pose (the head turns during the moves).
        var pool = new List<FrameCandidate>();
        foreach (var observation in report.Frames)
        {
            if (observation is { FaceCount: 1, QualityOk: true, Face: not null } && chosen.All(frame => frame.Index != observation.Index))
                pool.Add(new FrameCandidate(observation.Index, observation.TimestampMs, 0));
        }

        if (!chosen.Any(frame => frame.TimestampMs >= movesStart && frame.TimestampMs <= movesEnd))
        {
            var inside = pool.Where(frame => frame.TimestampMs >= movesStart && frame.TimestampMs <= movesEnd).ToList();
            if (inside.Count == 0)
                return null;

            var pick = PickSpread(inside, chosen, top);
            chosen.Add(pick);
            pool.Remove(pick);
        }

        while (chosen.Count < target)
        {
            if (pool.Count == 0)
                return null;

            var pick = PickSpread(pool, chosen, top);
            chosen.Add(pick);
            pool.Remove(pick);
        }

        return [.. chosen.Select(frame => frame.Index).Order()];
    }

    /// <summary>One of the <paramref name="top"/> frames of <paramref name="pool"/> that are farthest in time from the frames already chosen, drawn at random.</summary>
    private FrameCandidate PickSpread(List<FrameCandidate> pool, List<FrameCandidate> chosen, int top)
    {
        var farthest = pool
            .Select(frame => (Frame: frame, Distance: chosen.Min(earlier => Math.Abs(frame.TimestampMs - earlier.TimestampMs))))
            .OrderByDescending(entry => entry.Distance)
            .ThenBy(entry => entry.Frame.Index)
            .Take(top)
            .ToList();
        return farthest[_draw(farthest.Count)].Frame;
    }
}
