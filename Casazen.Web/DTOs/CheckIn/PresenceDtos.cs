using System.Text.Json.Serialization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.CheckIn;

/// <summary>
/// The presence challenge the guest's page receives (<c>POST api/public/checkin/{token}/presence-challenge</c>, E72/F01/US01/T01, NF-192, ADR-013-R17): the proof to send
/// back with the frames (<c>challengeToken</c>), the moves in the order they are asked (<c>steps</c>: <c>turn_left</c>, <c>turn_right</c>, <c>look_up</c> or
/// <c>look_down</c>, as the picture sent to the server shows them), the time of the lead-in and of each move in milliseconds (the recording starts at 0, the first move
/// at <c>leadInMs</c>), when the challenge expires, and the limits of the sequence: the rate to capture at, the most frames and the longest side of a frame in pixels.
/// Every number is the policy's or the section <c>IdentityVerification</c>'s. The answer is never cached.
/// </summary>
public sealed class PresenceChallengeDto
{
    public string ChallengeToken { get; set; } = string.Empty;

    public IReadOnlyList<string> Steps { get; set; } = [];

    public int StepMs { get; set; }

    public int LeadInMs { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int TargetFps { get; set; }

    public int MaxFrames { get; set; }

    public int MaxFrameSide { get; set; }

    public static PresenceChallengeDto From(PresenceChallengeIssue challenge) => new()
    {
        ChallengeToken = challenge.ChallengeToken,
        Steps = challenge.Steps,
        StepMs = challenge.StepMs,
        LeadInMs = challenge.LeadInMs,
        ExpiresAt = challenge.ExpiresAt,
        TargetFps = challenge.TargetFps,
        MaxFrames = challenge.MaxFrames,
        MaxFrameSide = challenge.MaxFrameSide,
    };
}

/// <summary>
/// The answer of the presence (<c>POST api/public/checkin/{token}/presence</c>, E73/F01/US01/T02, NF-194): <c>result</c> is <c>ok</c> (verified), <c>retry</c> (the one guided retry:
/// record again following <c>guidance</c>, with a new challenge and a new <c>Idempotency-Key</c>) or <c>operator</c> (a person decides; the request for a person is always open);
/// <c>guidance</c> the codes the page translates (<c>presence_too_short</c>, <c>presence_timing</c>, <c>presence_quality</c>, <c>presence_single_face</c>,
/// <c>presence_challenge_not_followed</c>), empty unless <c>retry</c>; <c>guidedRetryAvailable</c> true when this answer offers the guided retry (the <c>guidedRetryAvailable</c> of
/// <c>state</c> says whether one is still left); <c>state</c> the state of the verification after the answer. The same request sent again with the same <c>Idempotency-Key</c> gets the
/// same answer, without a second attempt. On <c>ok</c> only, as the selfie step of today: <c>lockCode</c> (also e-mailed) or <c>lockCodeHeld</c> (<c>tourist_tax_due</c>: the code
/// comes when the same request is sent again, same key, after the payment). Never a picture, a frame or a score.
/// </summary>
public sealed class PresenceStepResponseDto
{
    public string Result { get; set; } = string.Empty;

    public IReadOnlyList<string> Guidance { get; set; } = [];

    public bool GuidedRetryAvailable { get; set; }

    public PublicIdentityVerificationStateDto State { get; set; } = new();

    /// <summary>The lock code of the property, only on <c>ok</c> (absent otherwise).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LockCode { get; set; }

    /// <summary>Why an <c>ok</c> came with no lock code although the property has one (<c>tourist_tax_due</c>); absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LockCodeHeld { get; set; }

    public static PresenceStepResponseDto From(PresenceStepResult result) => new()
    {
        Result = result.Result ?? IdentityVerificationGuestResults.Operator,
        Guidance = result.Guidance,
        GuidedRetryAvailable = result.GuidedRetryAvailable,
        State = result.State is { } state ? PublicIdentityVerificationStateDto.From(state) : new PublicIdentityVerificationStateDto(),
        LockCode = result.LockCode,
        LockCodeHeld = result.LockCodeHeld,
    };
}
