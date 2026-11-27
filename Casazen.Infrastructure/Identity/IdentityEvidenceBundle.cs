using Casazen.Core.Services;

namespace Casazen.Infrastructure.Identity;

// E73/F01/US01/T01 (NF-193, NF-194, ADR-013-R18, R19, decisions D7 and D8): what the decision engine decides on. A plain record of the outcomes of the checks of ONE attempt of
// one session: codes, flags and the numbers the policy compares, never a picture, a frame, a value read on a document or a face representation. Every threshold in it comes
// from the versioned policy file (the closure fills it); the engine holds none.

/// <summary>How one check of the evidence came out. The default, <see cref="NotRun"/>, is never a pass: a check that was not reached cannot approve anybody.</summary>
public enum IdentityCheckStatus
{
    /// <summary>The check was not reached (an earlier step stopped the attempt) or was never filled in: never a pass.</summary>
    NotRun = 0,

    /// <summary>The check was made and holds.</summary>
    Passed = 1,

    /// <summary>The check was made and does not hold.</summary>
    Failed = 2,

    /// <summary>The check could not be made (a model was not there, failed or ran out of time): a technical fault, never a pass.</summary>
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

/// <summary>
/// The coherence of the document (NF-190, D11: the reliability of a document is coherence only), summed up for the decision. The default, <see cref="Unknown"/>, is never a pass:
/// a check that could not be made (a field not read, nothing kept to read again, as in Ticino) does not vouch for the document.
/// </summary>
public enum IdentityDocumentCoherence
{
    /// <summary>The coherence could not be told: never a pass.</summary>
    Unknown = 0,

    /// <summary>Every check of the coherence that the format requires passed.</summary>
    Passed = 1,

    /// <summary>A field the policy calls identifying reads differently on the two sides (or against the MRZ).</summary>
    IdentifyingConflict = 2,

    /// <summary>A check digit of the MRZ does not hold.</summary>
    MrzInvalid = 3,

    /// <summary>The document expires before the day of the stay.</summary>
    Expired = 4,

    /// <summary>
    /// A value declared for the booking (the names, the birth date, the document number or the citizenship) is there and differs from the document, in a format that
    /// requires <c>booking_consistency</c>: a known conflict, never a pass (IDENT-DECLARED). Nothing declared is no conflict (<c>not_applicable</c>).
    /// </summary>
    BookingConflict = 5,
}

/// <summary>Whether the models could run for the attempt. Anything but <see cref="None"/> is a fault, and a fault never approves (D8).</summary>
public enum IdentityModelsFault
{
    /// <summary>No fault: every model that was asked ran.</summary>
    None = 0,

    /// <summary>The models are not loaded (missing, a hash that does not match, still loading): the verification is <c>unavailable</c>.</summary>
    NotReady = 1,

    /// <summary>A model failed while it ran (an exception): <c>technical_error</c>.</summary>
    Failed = 2,

    /// <summary>The time of the attempt (<c>IdentityVerification:Presence:SequenceTimeoutMs</c>) was over: <c>technical_error</c>.</summary>
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
    /// <summary>The summary of <paramref name="report"/> with the thresholds of <paramref name="policy"/> (the very policy the matcher ran with).</summary>
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
    /// <summary>
    /// The checks of the presence the decision requires, in their order (the order of the validation, then the liveness of the frames the server chose): every one of them must
    /// be <see cref="IdentityCheckStatus.Passed"/> for an approval, and they are independent conditions (D7): none of them is weighed against another or against the similarity.
    /// </summary>
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
