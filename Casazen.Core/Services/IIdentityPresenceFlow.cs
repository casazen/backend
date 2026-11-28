using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IIdentityPresenceFlow
{
    /// <summary>
    /// Issues the challenge of the next attempt of <paramref name="session"/> (the session the public endpoint found by its token, tracked, with its booking), under
    /// the same row lock as the pose challenge of the legacy flow, and stores it in <c>GuestCheckInSession.DocumentCheckJson</c> (member <c>presence</c>: codes and
    /// times only, never a frame or a score). A new challenge <b>replaces</b> the stored one, so the proof of the previous one stops working; the attempt of the
    /// challenge is the attempts made plus the guided retries used plus one.
    /// <para>
    /// The answer is <c>null</c>, and nothing is stored, unless: both flags are on, the identity step was opened and the verification is not closed, the guest did not choose a person (the assisted path), the biometric
    /// consent is the current one, the document step is complete, the verification is not with a person, the decision policy is ready (its file valid and pinned), the
    /// models are ready and the section <c>IdentityVerification</c> is valid (<c>IdentityVerificationOptions.Validate()</c> names no setting: one limit out of its range
    /// makes the whole section not available, so nothing is announced from it). The page then offers the assisted path (409 <c>presence_challenge_unavailable</c>, or
    /// <c>identity_already_closed</c> for a closed verification). Every number of the challenge comes from the policy and from the section <c>IdentityVerification</c>,
    /// never from the code.
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

    /// <summary>
    /// Closes one attempt of the presence (E73/F01/US01/T02, NF-194, ADR-013-R17, R18, R19, decision D8): the frames of <paramref name="submission"/> are read in memory, validated
    /// against the challenge of the session, the server chooses the frames for the liveness and the match, the versioned policy decides, and the outcome is written atomically
    /// (one lock of the row of the session, one save). The same <see cref="PresenceSubmission.IdempotencyKey"/> always gets the same answer and never a second outcome: a
    /// replay is answered first, without reading a frame, also after the challenge expired. The outcomes are the approval (only in a segment that allows it), one guided retry for
    /// correctable defects, or a person; a fault of a model, a timeout or a policy that is not usable never approves, and the request for a person stays open.
    /// <para>
    /// The answer is <see cref="PresenceStepStatus.Done"/> with the result (<c>ok</c>, <c>retry</c> or <c>operator</c>), or the status of a request that was not run (nothing is
    /// written then: the challenge is not consumed). Behind <c>Features:CheckInIdentityVerification</c> and <c>Features:CheckInIdentitySelfHosted</c>.
    /// </para>
    /// </summary>
    /// <param name="session">The session the public endpoint found by its token, tracked, with its booking, its property and its guest.</param>
    /// <param name="submission">The parts of the request; the caller owns its streams and disposes them.</param>
    /// <param name="ct">The cancellation of the request.</param>
    Task<PresenceStepResult> SubmitPresenceSequenceAsync(GuestCheckInSession session, PresenceSubmission submission, CancellationToken ct);
}

/// <summary>
/// One frame of the presence as the endpoint received it (<c>POST api/public/checkin/{token}/presence</c>, part <c>frames</c>): the stream of its content, the length the client
/// declared and the content type it declared (carried, never trusted). It names no web type, so the flow stays free of ASP.NET.
/// </summary>
/// <param name="Content">The content of the part, read once from its current position.</param>
/// <param name="Length">The length in bytes the client declared.</param>
/// <param name="ContentType">The content type the client declared.</param>
public sealed record PresenceFrameUpload(Stream Content, long Length, string? ContentType);

/// <summary>
/// One attempt of the presence as the guest's page sends it (E73/F01/US01/T02): the explicit key of the attempt (<c>Idempotency-Key</c>), the proof of the challenge, the
/// timestamps of the frames (a JSON array of integers in milliseconds, the first 0, strictly increasing), the frames, and, for a property that keeps no document (Ticino), the
/// pictures of the document read before (the front, and the back of a document of two sides), recognised by their SHA-256.
/// </summary>
public sealed record PresenceSubmission(
    string? IdempotencyKey,
    string? ChallengeToken,
    string? TimestampsJson,
    IReadOnlyList<PresenceFrameUpload> Frames,
    Stream? DocumentPhoto = null,
    Stream? DocumentPhotoBack = null);

public enum PresenceStepStatus
{
    Done = 1,

    /// <summary>No valid <c>Idempotency-Key</c> (400 <c>idempotency_key_required</c>).</summary>
    IdempotencyKeyRequired = 2,

    AlreadyClosed = 3,

    ChallengeRequired = 4,

    ChallengeExpired = 5,

    ConsentRequired = 6,

    DocumentRequired = 7,

    DocumentPhotoRequired = 8,

    DocumentPhotoMismatch = 9,

    SequenceInvalid = 10,

    SequenceTooLarge = 11,

    SessionUnavailable = 12,
}

/// <summary>
/// The answer of the presence (E73/F01/US01/T02): the status and, when <see cref="PresenceStepStatus.Done"/>, the result the guest sees (<c>ok</c>, <c>retry</c> or <c>operator</c>),
/// the guidance codes of a guided retry, whether the answer offers the guided retry, the state of the verification and, on <c>ok</c> only, the lock code (or why it is held back).
/// <paramref name="Replayed"/> says the answer is the one of an attempt already closed with the same key (the body is the same; nothing was run).
/// </summary>
public sealed record PresenceStepResult(
    PresenceStepStatus Status,
    string? Result,
    IReadOnlyList<string> Guidance,
    bool GuidedRetryAvailable,
    IdentityVerificationState? State,
    string? LockCode = null,
    string? LockCodeHeld = null,
    bool Replayed = false)
{
    public bool OperatorPathOpen => true;
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

public enum PresenceTokenStatus
{
    Valid = 1,

    Required = 2,

    Expired = 3,
}
