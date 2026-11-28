using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Identity;

// Every check is an independent AND condition: no score makes up for another, a fault or an unknown never approves, and nothing is rejected: a person decides.
public sealed class IdentityDecisionEngine
{
    public IdentityDecision Decide(IdentityEvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var presence = PresenceOf(bundle);
        var mode = bundle.Segment is { AutoApprovalAllowed: true } ? IdentityDecisionMode.Automatic : IdentityDecisionMode.Observation;
        var documentPassed = bundle.DocumentComplete && bundle.DocumentCoherence is IdentityDocumentCoherence.Passed or IdentityDocumentCoherence.PassedWithoutBooking;
        var assurancePassed = AssuranceSatisfied(bundle);
        var reasons = Reasons(bundle, presence, assurancePassed);

        if (bundle.PolicyState != IdentityPolicyState.Ready || bundle.ModelsFault == IdentityModelsFault.NotReady)
            return Review([.. reasons, Processing(VerificationReasons.Results.Unavailable)], IdentityOutcomeCodes.Unavailable, mode);

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

        if (!documentPassed)
            return Review(reasons, IdentityOutcomeCodes.Review, mode);

        if (bundle.Match is not { State: MatchState.Ok, AcceptPassed: true })
            return Review(reasons, IdentityOutcomeCodes.Review, mode);

        if (!assurancePassed)
            return Review(reasons, IdentityOutcomeCodes.Review, mode);

        if (presence.Any(check => check.Status != IdentityCheckStatus.Passed))
            return Review(reasons, IdentityOutcomeCodes.Review, mode);

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

    private static bool AssuranceSatisfied(IdentityEvidenceBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.FormatId))
            return false;

        if (string.Equals(bundle.Segment?.ReasonCode, IdentitySegmentReasonCodes.AssuranceRequiresChip, StringComparison.Ordinal))
            return false;

        var required = RankOf(bundle.Segment?.RequiredAssurance ?? IdentityAssuranceLevels.Consistency);
        return required >= 0 && RankOf(bundle.ProvidedAssurance) >= required;
    }

    private static int RankOf(string? level)
    {
        return level switch
        {
            IdentityAssuranceLevels.Consistency => 0,
            IdentityAssuranceLevels.Cryptographic => 1,
            _ => -1,
        };
    }

    private static List<VerificationReason> Reasons(IdentityEvidenceBundle bundle, List<CheckOutcome> presence, bool assurancePassed)
    {
        var reasons = new List<VerificationReason>(presence.Count + 5);
        foreach (var check in presence)
            reasons.Add(new VerificationReason(check.Check, ResultOf(check.Status), Round(check.Value), check.Threshold));

        reasons.Add(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, CoherenceResult(bundle)));

        // The log outlives the checks kept on the session: this reason is the lasting trace that nothing was compared with the booking.
        if (bundle.DocumentComplete && bundle.DocumentCoherence == IdentityDocumentCoherence.PassedWithoutBooking)
            reasons.Add(new VerificationReason(VerificationReasons.Checks.BookingConsistency, VerificationReasons.Results.NotApplicable));

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

    private static string CoherenceResult(IdentityEvidenceBundle bundle)
    {
        if (!bundle.DocumentComplete)
            return VerificationReasons.Results.NotRun;

        return bundle.DocumentCoherence switch
        {
            IdentityDocumentCoherence.Passed or IdentityDocumentCoherence.PassedWithoutBooking => VerificationReasons.Results.Passed,
            IdentityDocumentCoherence.IdentifyingConflict => VerificationReasons.Results.Failed,
            IdentityDocumentCoherence.MrzInvalid => VerificationReasons.Results.CheckDigitsInvalid,
            IdentityDocumentCoherence.Expired => VerificationReasons.Results.Expired,
            IdentityDocumentCoherence.BookingConflict => VerificationReasons.Results.BookingMismatch,
            _ => VerificationReasons.Results.NotRun,
        };
    }

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
