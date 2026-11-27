using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The decision of the versioned policy for one attempt of the self-hosted identity verification (E73/F01/US01/T01, NF-193, NF-194, ADR-013-R18, R19, decisions D4, D7, D8).
/// <b>Pure</b>: no dependency, no field, no I/O, no clock, no logger, no randomness; the same <see cref="IdentityEvidenceBundle"/> (which carries the policy's numbers and the
/// segment) always gives the same <see cref="IdentityDecision"/>. It holds no decision number: the verdicts of the checks and of the match come in the bundle, filled from the
/// policy file by the closure.
/// </summary>
/// <remarks>
/// <para><b>The rules, in this order</b> (the first that applies decides):</para>
/// <list type="number">
/// <item><description><b>A fault never approves.</b> The policy is not ready, or the models are not loaded: a person reviews, outcome <c>unavailable</c>. A model failed or the time was
/// over (a check or the match that could not be made included): a person reviews, outcome <c>technical_error</c>. The reasons carry <c>processing</c>.</description></item>
/// <item><description><b>A failure of the presence.</b> When every failure is correctable (a new recording guided by its codes can fix it, and at least one code tells the guest what to
/// change) and the guided retries used are fewer than <c>closure.guidedRetries</c>: one guided retry with the guidance codes of the failures, whatever the document, the format and the
/// match say: they are judged by the attempt that follows, as ever, and a guided retry is never an approval (its shadow decision is the review, it has no outcome code and closes nothing).
/// Otherwise a person reviews: the same correctable defects after the guided retry are reviewed with their reasons.</description></item>
/// <item><description><b>The document.</b> A coherence that is not a pass (an identifying conflict, an invalid MRZ, an expired document, or a coherence that could not be told)
/// or a document that is not complete: review, never correctable.</description></item>
/// <item><description><b>The similarity.</b> In the review band, under the floor, a weak frame, no portrait, or no comparison at all: review, never correctable.</description></item>
/// <item><description><b>The format and the assurance.</b> No format, or a format that does not give on the channel the assurance the segment requires
/// (<c>assurance_requires_chip</c>): review with <c>assurance_level</c>.</description></item>
/// <item><description><b>Everything passed</b> (every check of <see cref="IdentityEvidenceBundle.PresenceChecks"/>, the document, the match and the assurance): the policy would
/// approve (shadow approval). The outcome is the automatic approval ONLY when the segment allows it (<see cref="SegmentResolution.AutoApprovalAllowed"/>: qualified for the policy
/// and the weights in force, with the platform permission on); otherwise a person reviews, with the reason <c>observation_mode</c> whose result is the reason code of the segment
/// (for example <c>segment_observation</c> or <c>auto_approval_disabled</c>), in observation.</description></item>
/// </list>
/// <para><b>No compensation</b> (D7): the timing, the single face, the quality, the continuity, the challenge, the liveness and the similarity are independent AND conditions. No
/// score is combined with another, nothing is weighed or averaged across kinds: a similarity of 0.99 never makes up for a failed presence check.</para>
/// <para><b>No rejection</b> (D8): the outcomes are the approval, the guided retry and the review (<see cref="IdentityFinalDecision"/>).</para>
/// </remarks>
public sealed class IdentityDecisionEngine
{
    /// <summary>Decides <paramref name="bundle"/>. Never throws for a bundle that is not null; a value that is not one of its enum is never a pass.</summary>
    public IdentityDecision Decide(IdentityEvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var presence = PresenceOf(bundle);
        var mode = bundle.Segment is { AutoApprovalAllowed: true } ? IdentityDecisionMode.Automatic : IdentityDecisionMode.Observation;
        var documentPassed = bundle.DocumentComplete && bundle.DocumentCoherence == IdentityDocumentCoherence.Passed;
        var assurancePassed = AssuranceSatisfied(bundle);
        var reasons = Reasons(bundle, presence, assurancePassed);

        // a. The policy or the models are not there: a person decides, never an approval.
        if (bundle.PolicyState != IdentityPolicyState.Ready || bundle.ModelsFault == IdentityModelsFault.NotReady)
            return Review([.. reasons, Processing(VerificationReasons.Results.Unavailable)], IdentityOutcomeCodes.Unavailable, mode);

        // a. A model failed or the time was over, or a check or the match could not be made: a person decides, never an approval.
        if (bundle.ModelsFault != IdentityModelsFault.None
            || presence.Any(check => check.Status == IdentityCheckStatus.Unavailable)
            || bundle.Match?.State == MatchState.Unavailable)
        {
            var result = bundle.ModelsFault == IdentityModelsFault.TimedOut ? VerificationReasons.Results.Timeout : VerificationReasons.Results.Error;
            return Review([.. reasons, Processing(result)], IdentityOutcomeCodes.TechnicalError, mode);
        }

        // b. A failure of the presence: one guided retry when a new recording can fix every failure and tells the guest what to change, whatever the document, the format and the
        // match say (the order of the rules is the brief's). A guided retry decides nothing else: its shadow decision is the review and it closes nothing, and the next attempt meets
        // the same document, format and match again.
        var failed = presence.Where(check => check.Status == IdentityCheckStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            var guidance = failed
                .Select(check => check.GuidanceCode)
                .OfType<string>()
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var onlyCorrectable = failed.All(check => check.Correctable) && guidance.Count > 0;
            if (onlyCorrectable && bundle.GuidedRetriesUsed < bundle.GuidedRetriesAllowed)
                return new IdentityDecision(IdentityFinalDecision.GuidedRetry, IdentityShadowDecision.Review, reasons, guidance, null, mode);

            return Review(reasons, IdentityOutcomeCodes.Review, mode);
        }

        // c. The document: an identifying conflict, an invalid MRZ, an expired document, a coherence that could not be told, a document not complete.
        if (!documentPassed)
            return Review(reasons, IdentityOutcomeCodes.Review, mode);

        // d. The similarity: in the review band, under the floor, a weak frame, no portrait, or no comparison at all.
        if (bundle.Match is not { State: MatchState.Ok, AcceptPassed: true })
            return Review(reasons, IdentityOutcomeCodes.Review, mode);

        // e. No format, or a format that does not give the assurance the segment requires on the channel.
        if (!assurancePassed)
            return Review(reasons, IdentityOutcomeCodes.Review, mode);

        // f. Every check of the presence passed: a check that was not run, or whose status is not a pass, never approves.
        if (presence.Any(check => check.Status != IdentityCheckStatus.Passed))
            return Review(reasons, IdentityOutcomeCodes.Review, mode);

        // f. The policy would approve. The approval is the outcome only where the segment allows it; elsewhere it is the shadow decision and a person decides.
        if (bundle.Segment is { AutoApprovalAllowed: true })
            return new IdentityDecision(IdentityFinalDecision.AutoApproved, IdentityShadowDecision.Approve, reasons, [], IdentityOutcomeCodes.AutoApproved, IdentityDecisionMode.Automatic);

        var segmentReason = bundle.Segment?.ReasonCode is { Length: > 0 } reasonCode ? reasonCode : IdentitySegmentReasonCodes.SegmentObservation;
        return new IdentityDecision(
            IdentityFinalDecision.Review,
            IdentityShadowDecision.Approve,
            [.. reasons, new VerificationReason(VerificationReasons.Checks.ObservationMode, segmentReason)],
            [],
            IdentityOutcomeCodes.Review,
            IdentityDecisionMode.Observation);
    }

    private static IdentityDecision Review(IReadOnlyList<VerificationReason> reasons, string outcomeCode, IdentityDecisionMode mode)
    {
        return new IdentityDecision(IdentityFinalDecision.Review, IdentityShadowDecision.Review, reasons, [], outcomeCode, mode);
    }

    private static VerificationReason Processing(string result)
    {
        return new VerificationReason(VerificationReasons.Checks.Processing, result);
    }

    /// <summary>
    /// The checks of the presence the decision reads: every check of <see cref="IdentityEvidenceBundle.PresenceChecks"/> in its order (one that is missing is
    /// <see cref="IdentityCheckStatus.NotRun"/>), then any other check the bundle carries, in its order. A check given more than once is its worst entry (unavailable, then failed,
    /// then not run, then passed), and its failure is correctable only when every failed entry is.
    /// </summary>
    private static List<CheckOutcome> PresenceOf(IdentityEvidenceBundle bundle)
    {
        var given = bundle.Presence ?? [];
        var names = IdentityEvidenceBundle.PresenceChecks
            .Concat(given.Select(check => check.Check).Where(name => !string.IsNullOrEmpty(name)))
            .Distinct(StringComparer.Ordinal);

        var checks = new List<CheckOutcome>();
        foreach (var name in names)
        {
            var entries = given.Where(check => string.Equals(check.Check, name, StringComparison.Ordinal)).ToList();
            if (entries.Count == 0)
            {
                checks.Add(new CheckOutcome(name, IdentityCheckStatus.NotRun));
                continue;
            }

            var worst = entries.OrderByDescending(check => Severity(check.Status)).First();
            var failures = entries.Where(check => check.Status == IdentityCheckStatus.Failed).ToList();
            checks.Add(worst with
            {
                Correctable = failures.Count > 0 && failures.All(check => check.Correctable),
                GuidanceCode = failures.Select(check => check.GuidanceCode).FirstOrDefault(code => !string.IsNullOrEmpty(code)) ?? worst.GuidanceCode,
            });
        }

        return checks;
    }

    /// <summary>How bad a status is, to keep the worst entry of a check: a value that is not one of the enum counts as unavailable.</summary>
    private static int Severity(IdentityCheckStatus status)
    {
        return status switch
        {
            IdentityCheckStatus.Passed => 0,
            IdentityCheckStatus.NotRun => 1,
            IdentityCheckStatus.Failed => 2,
            _ => 3,
        };
    }

    /// <summary>
    /// The format gives, on the channel, at least the assurance the segment requires: no format, a level the registry does not give, a level that is not one, or a segment that
    /// says <c>assurance_requires_chip</c> is not satisfied. Without a segment the level required is <c>consistency</c>, the level of a segment created with no qualification.
    /// </summary>
    private static bool AssuranceSatisfied(IdentityEvidenceBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.FormatId))
            return false;

        if (string.Equals(bundle.Segment?.ReasonCode, IdentitySegmentReasonCodes.AssuranceRequiresChip, StringComparison.Ordinal))
            return false;

        var required = RankOf(bundle.Segment?.RequiredAssurance ?? IdentityAssuranceLevels.Consistency);
        return required >= 0 && RankOf(bundle.ProvidedAssurance) >= required;
    }

    /// <summary>The order of the levels of assurance (the one of <c>IdentitySegmentService</c>): <c>consistency</c> below <c>cryptographic</c>; -1 for a code that is not a level.</summary>
    private static int RankOf(string? level)
    {
        return level switch
        {
            IdentityAssuranceLevels.Consistency => 0,
            IdentityAssuranceLevels.Cryptographic => 1,
            _ => -1,
        };
    }

    /// <summary>The reasons of the decision, one per check in a fixed order: the presence, the coherence of the document, the similarity, the format when there is none, the assurance.</summary>
    private static List<VerificationReason> Reasons(IdentityEvidenceBundle bundle, List<CheckOutcome> presence, bool assurancePassed)
    {
        var reasons = new List<VerificationReason>(presence.Count + 5);
        foreach (var check in presence)
            reasons.Add(new VerificationReason(check.Check, ResultOf(check.Status), Round(check.Value), check.Threshold));

        reasons.Add(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, CoherenceResult(bundle)));
        reasons.Add(SimilarityReason(bundle.Match));
        if (string.IsNullOrWhiteSpace(bundle.FormatId))
            reasons.Add(new VerificationReason(VerificationReasons.Checks.DocumentFormat, VerificationReasons.Results.Failed));

        reasons.Add(new VerificationReason(
            VerificationReasons.Checks.AssuranceLevel,
            assurancePassed ? VerificationReasons.Results.Passed : VerificationReasons.Results.Failed));
        return reasons;
    }

    private static string ResultOf(IdentityCheckStatus status)
    {
        return status switch
        {
            IdentityCheckStatus.Passed => VerificationReasons.Results.Passed,
            IdentityCheckStatus.Failed => VerificationReasons.Results.Failed,
            IdentityCheckStatus.NotRun => VerificationReasons.Results.NotRun,
            _ => VerificationReasons.Results.Unavailable,
        };
    }

    /// <summary>The result of the coherence of the document: a coherence that could not be told, or a document not complete, is <c>not_run</c> (never a pass).</summary>
    private static string CoherenceResult(IdentityEvidenceBundle bundle)
    {
        if (!bundle.DocumentComplete)
            return VerificationReasons.Results.NotRun;

        return bundle.DocumentCoherence switch
        {
            IdentityDocumentCoherence.Passed => VerificationReasons.Results.Passed,
            IdentityDocumentCoherence.IdentifyingConflict => VerificationReasons.Results.Failed,
            IdentityDocumentCoherence.MrzInvalid => VerificationReasons.Results.CheckDigitsInvalid,
            IdentityDocumentCoherence.Expired => VerificationReasons.Results.Expired,
            _ => VerificationReasons.Results.NotRun,
        };
    }

    /// <summary>
    /// The reason of the similarity with the number the reviewer needs: the aggregate against <c>match.accept</c> when it passed or is in the review band, the weakest frame against
    /// <c>match.minEachFrame</c> when the aggregate reached the acceptance but a frame did not, the aggregate against <c>match.reviewFloor</c> under the floor. The verdicts are the
    /// matcher's (<see cref="MatchSummary.AcceptPassed"/>, <see cref="MatchSummary.InReviewBand"/>); the comparison below only chooses which number to show.
    /// </summary>
    private static VerificationReason SimilarityReason(MatchSummary? match)
    {
        const string check = VerificationReasons.Checks.Similarity;
        if (match is null)
            return new VerificationReason(check, VerificationReasons.Results.NotRun);

        return match.State switch
        {
            MatchState.Ok when match.AcceptPassed => new VerificationReason(check, VerificationReasons.Results.Passed, Round(match.Aggregate), match.Accept),
            MatchState.Ok when match.InReviewBand => new VerificationReason(check, VerificationReasons.Results.BetweenThresholds, Round(match.Aggregate), match.Accept),
            MatchState.Ok when match.Aggregate >= match.Accept => new VerificationReason(check, VerificationReasons.Results.Failed, Round(match.Min), match.MinEachFrame),
            MatchState.Ok => new VerificationReason(check, VerificationReasons.Results.Failed, Round(match.Aggregate), match.ReviewFloor),
            MatchState.NoPortrait => new VerificationReason(check, VerificationReasons.Results.Failed),
            _ => new VerificationReason(check, VerificationReasons.Results.Unavailable),
        };
    }

    private static double? Round(double? value)
    {
        return value is { } number && double.IsFinite(number) ? Math.Round(number, 4) : null;
    }
}
