using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// One reason a presence sequence was not valid (E72/F01/US01/T03, NF-192): what the guest is asked to change, which check found it, whether a new recording can fix it
/// and its result. Codes only, never a value of a person.
/// </summary>
/// <param name="Code">
/// The guidance the page shows, a code of <see cref="Casazen.Core.Identity.IdentityGuidanceCodes"/>; null for a failure of the processing, where nothing is asked of the
/// guest (a person decides).
/// </param>
/// <param name="Check">The check, a code of <see cref="VerificationReasons.Checks"/>: <c>presence_timing</c>, <c>presence_single_face</c>, <c>presence_quality</c>, <c>presence_continuity</c>, <c>presence_challenge</c>, or <c>processing</c>.</param>
/// <param name="Correctable">True when a guided retry can fix it: the guest recorded too short, with someone in the background, in the dark, or did not follow the moves. False for what no retry explains: a recording longer than the time since the challenge, a still picture or a splice of two people, a failure of the processing.</param>
/// <param name="Result">The result of the check for the log, a code of <see cref="VerificationReasons.Results"/>: <c>failed</c>, or <c>unavailable</c> for a failure of the processing.</param>
public sealed record SequenceFailure(string? Code, string Check, bool Correctable, string Result = VerificationReasons.Results.Failed);

/// <summary>
/// What the validation saw in one frame: the faces the detector found, the largest of them (its box and landmarks in the pixels of the frame as it was received), the
/// pose of that head, and whether the frame is good enough to judge (one face, large enough, light and focus within the capture rules). Numbers of a person: in memory
/// for the request only, never logged, stored or put in a job; the server chooses the frames for the liveness and the match from them.
/// </summary>
/// <param name="Index">The place of the frame in the sequence.</param>
/// <param name="TimestampMs">The time of the frame in milliseconds from the first.</param>
/// <param name="FaceCount">How many faces the detector found.</param>
/// <param name="Face">The largest face, in the pixels of the frame; null when there is none.</param>
/// <param name="YawDegrees">The yaw of that face (positive to the right of the picture); <see cref="double.NaN"/> when it was not estimated (no face, or more than one).</param>
/// <param name="PitchDegrees">The pitch of that face (positive looking up); <see cref="double.NaN"/> when it was not estimated.</param>
/// <param name="QualityOk">True when the frame has exactly one face and passes the capture rules of the check-in.</param>
public sealed record FrameObservation(int Index, long TimestampMs, int FaceCount, DetectedFace? Face, double YawDegrees, double PitchDegrees, bool QualityOk);

/// <summary>How a step of the challenge was answered: the move asked, the furthest the head went in that direction inside the window of the step, and when the move was answered.</summary>
/// <param name="Step">The place of the step in the challenge, from 0.</param>
/// <param name="Action">The move asked, a code of <see cref="Casazen.Core.Identity.PresenceActions"/>.</param>
/// <param name="PeakDegrees">The most the pose departed from the baseline in the direction asked, inside the window of the step with its tolerance (0 when no frame had a pose there); negative when the head only went the other way.</param>
/// <param name="AnsweredAtMs">The time of the first frame at which the departure reached the minimum of the policy; null when the step was not answered.</param>
public sealed record StepPeak(int Step, string Action, double PeakDegrees, long? AnsweredAtMs);

/// <summary>
/// The verdict of the validation of one presence sequence (E72/F01/US01/T03, NF-192, ADR-013-R17): <see cref="Passed"/> only when no check found anything; every
/// failure is listed, in the order of the checks (timing, faces, quality, continuity, challenge). A sequence that could not be judged (the policy or the models are not
/// available, the time was over, a model failed) is never <see cref="Passed"/>: it has the one failure of the check <c>processing</c> with the result
/// <c>unavailable</c>, no frames and no steps, and a person decides. The report decides nothing about the outcome: it is the input of the decision (E73), which also asks
/// the liveness and the match.
/// </summary>
/// <param name="Passed">True when there is no failure.</param>
/// <param name="Failures">What was found, in the order of the checks.</param>
/// <param name="Frames">What each frame showed, in the order of the sequence; empty when the sequence could not be judged.</param>
/// <param name="StepPeaks">How each step of the challenge was answered, in the order of the challenge; empty when it could not be judged.</param>
public sealed record SequenceValidationReport(
    bool Passed,
    IReadOnlyList<SequenceFailure> Failures,
    IReadOnlyList<FrameObservation> Frames,
    IReadOnlyList<StepPeak> StepPeaks)
{
    /// <summary>True when the sequence could not be judged (the check <c>processing</c> failed): nothing says it is valid or that it is not.</summary>
    public bool IsUnavailable => Failures.Any(failure => string.Equals(failure.Check, VerificationReasons.Checks.Processing, StringComparison.Ordinal));

    /// <summary>True when the sequence did not pass and every reason is one a new recording can fix: the case for the one guided retry.</summary>
    public bool AllFailuresCorrectable => !Passed && Failures.All(failure => failure.Correctable);

    /// <summary>The report of a sequence that could not be judged: never passed, one failure of the check <c>processing</c>, result <c>unavailable</c>, not correctable.</summary>
    public static SequenceValidationReport NotAvailable()
    {
        return new SequenceValidationReport(
            false,
            [new SequenceFailure(null, VerificationReasons.Checks.Processing, Correctable: false, VerificationReasons.Results.Unavailable)],
            [],
            []);
    }
}
