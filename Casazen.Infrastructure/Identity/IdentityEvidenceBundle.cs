using Casazen.Core.Services;

namespace Casazen.Infrastructure.Identity;

// E73/F01/US01/T01 (NF-193, NF-194, ADR-013-R18, R19, decisions D7 and D8): what the decision engine decides on. A plain record of the outcomes of the checks of ONE attempt of
// one session: codes, flags and the numbers the policy compares, never a picture, a frame, a value read on a document or a face representation. Every threshold in it comes
// from the versioned policy file (the closure fills it); the engine holds none.

public enum IdentityCheckStatus
{
    NotRun = 0,

    Passed = 1,

    Failed = 2,

    Unavailable = 3,
}

/// <summary>
/// One check of the presence of the guest (E72: the timing, the single face, the quality, the continuity, the challenge, the liveness) as the decision sees it.
/// </summary>
/// <param name="Check">The check, a code of <see cref="VerificationReasons.Checks"/> (for example <c>presence_timing</c> or <c>liveness</c>).</param>
/// <param name="Status">How it came out.</param>
/// <param name="Correctable">True when a new recording guided by <paramref name="GuidanceCode"/> can fix a failure (too short, too dark, the moves not followed); false otherwise.</param>
/// <param name="GuidanceCode">What the guest is asked to change, a code of <c>IdentityGuidanceCodes</c>; null when nothing is asked of the guest.</param>
/// <param name="Value">The number measured, when the check has one (the lowest liveness score of the chosen frames): for the log of the decision only.</param>
/// <param name="Threshold">The threshold of the policy <paramref name="Value"/> was compared with.</param>
public sealed record CheckOutcome(
    string Check,
    IdentityCheckStatus Status,
    bool Correctable = false,
    string? GuidanceCode = null,
    double? Value = null,
    double? Threshold = null);

public enum IdentityDocumentCoherence
{
    Unknown = 0,

    Passed = 1,

    IdentifyingConflict = 2,

    MrzInvalid = 3,

    Expired = 4,
}

public enum IdentityModelsFault
{
    None = 0,

    NotReady = 1,

    Failed = 2,

    TimedOut = 3,
}

/// <summary>
/// The comparison of the portrait of the document with the frames the server chose (E72/F01/US02/T02, <see cref="MatchReport"/>) with the thresholds of the policy it was made with.
/// The verdicts (<paramref name="AcceptPassed"/>, <paramref name="InReviewBand"/>) are the matcher's: the engine compares no number itself.
/// </summary>
/// <param name="State">How the comparison came out; the numbers mean something only when it is <see cref="MatchState.Ok"/>.</param>
/// <param name="Aggregate">The similarities of the chosen frames combined by the frozen rule of the policy.</param>
/// <param name="Min">The lowest similarity of a single chosen frame.</param>
/// <param name="AcceptPassed">The aggregate reaches <c>match.accept</c> and every frame reaches <c>match.minEachFrame</c>.</param>
/// <param name="InReviewBand">The aggregate is from <c>match.reviewFloor</c> up to, not including, <c>match.accept</c>.</param>
/// <param name="Accept">The threshold <c>match.accept</c> of the policy.</param>
/// <param name="ReviewFloor">The threshold <c>match.reviewFloor</c> of the policy.</param>
/// <param name="MinEachFrame">The threshold <c>match.minEachFrame</c> of the policy.</param>
public sealed record MatchSummary(
    MatchState State,
    double Aggregate,
    double Min,
    bool AcceptPassed,
    bool InReviewBand,
    double Accept,
    double ReviewFloor,
    double MinEachFrame)
{
    public static MatchSummary From(MatchReport report, IdentityMatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(policy);
        return new MatchSummary(report.State, report.Aggregate, report.Min, report.AcceptPassed, report.InReviewBand, policy.Accept, policy.ReviewFloor, policy.MinEachFrame);
    }
}

/// <summary>
/// Everything the decision of one attempt rests on (E73/F01/US01/T01, NF-194): the state of the policy, the document, the format and the channel with the assurance the format
/// gives there, the checks of the presence, the match, the faults of the models, the segment and the guided retries. <see cref="IdentityDecisionEngine"/> turns it into an
/// <see cref="IdentityDecision"/>; the same bundle always gives the same decision.
/// </summary>
/// <param name="PolicyState">Whether the decision policy can be used (<see cref="IdentityPolicyState"/>); anything but ready is a review.</param>
/// <param name="PolicyReasonCode">Why it cannot (a code of <see cref="IdentityPolicyReasonCodes"/>), null when it is ready.</param>
/// <param name="DocumentComplete">Every side of the document was read and passed its checks.</param>
/// <param name="DocumentCoherence">The coherence of the document.</param>
/// <param name="FormatId">The format of the registry the document was read as; null when none (never approved).</param>
/// <param name="Channel">The family of the browser, a code of <c>IdentityChannels</c>.</param>
/// <param name="ProvidedAssurance">
/// The level of assurance the format gives on the channel (a code of <c>IdentityAssuranceLevels</c>, <c>DocumentFormatRegistry.AssuranceFor</c>); null when the format or the channel is
/// not admitted, which satisfies nothing.
/// </param>
/// <param name="Presence">The checks of the presence; a check of <see cref="PresenceChecks"/> that is missing here counts as <see cref="IdentityCheckStatus.NotRun"/>.</param>
/// <param name="Match">The comparison with the portrait; null when it was not made (an earlier check stopped the attempt).</param>
/// <param name="ModelsFault">Whether the models could run.</param>
/// <param name="Segment">What the segment of the property, the format and the channel allows (<see cref="IIdentitySegmentService.ResolveAsync"/>); null when it was not resolved.</param>
/// <param name="GuidedRetriesUsed">The guided retries the session already used (<c>GuestCheckInSession.IdentityGuidedRetries</c>).</param>
/// <param name="GuidedRetriesAllowed">The guided retries the policy offers (<c>closure.guidedRetries</c>); 0 when the policy is not available.</param>
public sealed record IdentityEvidenceBundle(
    IdentityPolicyState PolicyState,
    string? PolicyReasonCode,
    bool DocumentComplete,
    IdentityDocumentCoherence DocumentCoherence,
    string? FormatId,
    string? Channel,
    string? ProvidedAssurance,
    IReadOnlyList<CheckOutcome> Presence,
    MatchSummary? Match,
    IdentityModelsFault ModelsFault,
    SegmentResolution? Segment,
    int GuidedRetriesUsed,
    int GuidedRetriesAllowed)
{
    public static IReadOnlyList<string> PresenceChecks { get; } =
    [
        VerificationReasons.Checks.PresenceTiming,
        VerificationReasons.Checks.PresenceSingleFace,
        VerificationReasons.Checks.PresenceQuality,
        VerificationReasons.Checks.PresenceContinuity,
        VerificationReasons.Checks.PresenceChallenge,
        VerificationReasons.Checks.Liveness,
    ];
}
