using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Identity;

// E73/F01/US01/T01 (NF-194, ADR-013-R18, R19, decision D8): what the decision engine answers. There is NO reject: the outcomes of the policy are the automatic approval, one guided
// retry for a correctable defect, or the review of a person with the reasons. The defaults of both enums are the review, so a decision that was never filled in fails closed.

/// <summary>The outcome of an attempt (D8). There is no rejection: what the policy cannot approve goes to a person.</summary>
public enum IdentityFinalDecision
{
    /// <summary>A person reviews the verification with the reasons of the decision. The default: a decision never filled in is a review.</summary>
    Review = 0,

    /// <summary>Every failure is correctable and the guided retry of the policy is still available: the guest records again, guided by the codes of the decision.</summary>
    GuidedRetry = 1,

    /// <summary>Every check passed and the segment allows the automatic approval: the guest is approved.</summary>
    AutoApproved = 2,
}

/// <summary>What the policy would have decided, recorded as the shadow outcome of the log row while a person decides (observation, ADR-013-R19). No rejection either.</summary>
public enum IdentityShadowDecision
{
    /// <summary>The policy would not approve. The default.</summary>
    Review = 0,

    /// <summary>The policy would approve: every check passed and the similarity reaches the acceptance of the policy.</summary>
    Approve = 1,
}

/// <summary>
/// The decision of the policy for one attempt (E73/F01/US01/T01): the outcome, the shadow outcome, the reasons in the shape of <c>BiometricVerificationLog.ReasonsJson</c>
/// (<c>{ check, result, value?, threshold? }</c>: codes and the numbers the policy compared, never a picture or a value read on a document), the guidance codes of a guided retry,
/// the terminal outcome code of the session and how the decision was taken.
/// </summary>
/// <param name="Final">The outcome.</param>
/// <param name="Shadow">What the policy would have decided.</param>
/// <param name="Reasons">One entry per check, in a fixed order, plus <c>processing</c> on a fault and <c>observation_mode</c> (with the reason code of the segment as its result) when the segment keeps an approval of the policy for a person.</param>
/// <param name="Guidance">What the guest is asked to change, codes of <c>IdentityGuidanceCodes</c>; empty unless <paramref name="Final"/> is <see cref="IdentityFinalDecision.GuidedRetry"/>.</param>
/// <param name="OutcomeCode">
/// The terminal outcome code of the session (<c>IdentityOutcomeCodes</c>): <c>auto_approved</c>, <c>review</c>, <c>technical_error</c> or <c>unavailable</c>; null for a guided retry,
/// which does not close the verification.
/// </param>
/// <param name="Mode">
/// <see cref="IdentityDecisionMode.Automatic"/> when the segment allows the automatic approval (the outcome of the policy is the outcome), <see cref="IdentityDecisionMode.Observation"/>
/// otherwise (a person decides whatever the policy says). Never <see cref="IdentityDecisionMode.Legacy"/>.
/// </param>
public sealed record IdentityDecision(
    IdentityFinalDecision Final,
    IdentityShadowDecision Shadow,
    IReadOnlyList<VerificationReason> Reasons,
    IReadOnlyList<string> Guidance,
    string? OutcomeCode,
    IdentityDecisionMode Mode)
{
    /// <summary>True when the decision closes the verification (an approval or a review); false for the guided retry.</summary>
    public bool ClosesTheVerification => Final != IdentityFinalDecision.GuidedRetry;

    /// <summary>The outcome of the log row: <see cref="BiometricOutcome.Accept"/> for the automatic approval, <see cref="BiometricOutcome.Escalate"/> otherwise (never a rejection).</summary>
    public BiometricOutcome LogOutcome => Final == IdentityFinalDecision.AutoApproved ? BiometricOutcome.Accept : BiometricOutcome.Escalate;

    /// <summary>The shadow outcome of the log row: <see cref="BiometricOutcome.Accept"/> when the policy would approve, <see cref="BiometricOutcome.Escalate"/> otherwise.</summary>
    public BiometricOutcome ShadowOutcome => Shadow == IdentityShadowDecision.Approve ? BiometricOutcome.Accept : BiometricOutcome.Escalate;
}
