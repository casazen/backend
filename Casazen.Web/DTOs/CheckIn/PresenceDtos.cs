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
