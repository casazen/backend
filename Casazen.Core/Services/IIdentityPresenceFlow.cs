using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The presence challenge of the self-hosted identity verification of one check-in session (E72/F01/US01/T01, NF-192, ADR-013-R17, decision D6): a short,
/// unpredictable request (more than one bit) with an expiry, bound to the session and to the attempt, that the guest answers with a sequence of frames. It is a new
/// interface that <see cref="IIdentityVerificationFlowService"/>'s implementation also implements (the same scoped instance), so the shared interface of the flow
/// stays as it is. Everything it does is behind <c>Features:CheckInIdentityVerification</c> and <c>Features:CheckInIdentitySelfHosted</c>, both off by default.
/// </summary>
public interface IIdentityPresenceFlow
{
    /// <summary>
    /// Issues the challenge of the next attempt of <paramref name="session"/> (the session the public endpoint found by its token, tracked, with its booking), under
    /// the same row lock as the pose challenge of the legacy flow, and stores it in <c>GuestCheckInSession.DocumentCheckJson</c> (member <c>presence</c>: codes and
    /// times only, never a frame or a score). A new challenge <b>replaces</b> the stored one, so the proof of the previous one stops working; the attempt of the
    /// challenge is the attempts made plus the guided retries used plus one.
    /// <para>
    /// The answer is <c>null</c>, and nothing is stored, unless: both flags are on, the identity step was opened and the verification is not closed, the guest did not choose a person (the assisted path), the biometric
    /// consent is the current one, the document step is complete, the verification is not with a person, the decision policy is ready (its file valid and pinned) and
    /// the models are ready. The page then offers the assisted path (409 <c>presence_challenge_unavailable</c>, or <c>identity_already_closed</c> for a closed
    /// verification). Every number of the challenge comes from the policy and from the section <c>IdentityVerification</c>, never from the code.
    /// </para>
    /// </summary>
    Task<PresenceChallengeIssue?> IssuePresenceChallengeAsync(GuestCheckInSession session, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="challengeToken"/> is the proof of the challenge stored on <paramref name="session"/> for its current attempt: the proof binds the session,
    /// the token of its link, the attempt, the time of the consent and the challenge, so a proof of another session, attempt, consent or challenge (the one a newer
    /// challenge replaced included) is <see cref="PresenceTokenStatus.Required"/>, and one of a challenge that expired is <see cref="PresenceTokenStatus.Expired"/>. It
    /// only validates: the challenge is <b>consumed</b> once, in a transaction, when the verification is closed (E73), under the lock of the session.
    /// </summary>
    /// <param name="session">The session, as it is stored now (the closure reads it again under the lock first).</param>
    /// <param name="challengeToken">The proof the page received with the challenge.</param>
    /// <param name="status">Why the proof is not valid, or <see cref="PresenceTokenStatus.Valid"/>.</param>
    /// <returns>True when <paramref name="status"/> is <see cref="PresenceTokenStatus.Valid"/>.</returns>
    bool TryValidatePresenceToken(GuestCheckInSession session, string? challengeToken, out PresenceTokenStatus status);
}

/// <summary>
/// A presence challenge as the guest's page receives it (<c>POST api/public/checkin/{token}/presence-challenge</c>): the proof to send back with the frames, the moves
/// in their order (codes of <c>PresenceActions</c>, as the picture sent to the server shows them), the time of the lead-in and of each move, when the challenge
/// expires, and the limits of the sequence the server accepts. All of them are the policy's and the options', never the code's.
/// </summary>
/// <param name="ChallengeToken">The proof, an opaque string bound to the session, the attempt and the challenge: sent back with the frames.</param>
/// <param name="Steps">The moves, in the order they are asked.</param>
/// <param name="StepMs">How long the guest has for each move, in milliseconds.</param>
/// <param name="LeadInMs">The time before the first move, in milliseconds (the recording starts at 0).</param>
/// <param name="ExpiresAt">When the challenge expires.</param>
/// <param name="TargetFps">The rate the page captures at, in frames per second.</param>
/// <param name="MaxFrames">The most frames the server takes: the smaller of the policy and <c>IdentityVerification:Presence:MaxFrames</c>.</param>
/// <param name="MaxFrameSide">The longest side of a frame in pixels (<c>IdentityVerification:Presence:MaxFrameSide</c>).</param>
public sealed record PresenceChallengeIssue(
    string ChallengeToken,
    IReadOnlyList<string> Steps,
    int StepMs,
    int LeadInMs,
    DateTimeOffset ExpiresAt,
    int TargetFps,
    int MaxFrames,
    int MaxFrameSide);

/// <summary>What <see cref="IIdentityPresenceFlow.TryValidatePresenceToken"/> found.</summary>
public enum PresenceTokenStatus
{
    /// <summary>The proof is the one of the challenge stored for the current attempt, and the challenge has not expired.</summary>
    Valid = 1,

    /// <summary>There is no valid proof: none sent, none stored, the proof is of another session, attempt, consent or challenge, or the stored challenge is of a past attempt (<c>presence_challenge_required</c>).</summary>
    Required = 2,

    /// <summary>The proof is the right one and the challenge expired (<c>presence_challenge_expired</c>): the page asks for a new one.</summary>
    Expired = 3,
}
