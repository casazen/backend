using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US01/T01, decision D7 (NF-193, AC-NF193-1): presence and similarity are independent AND conditions. Exhaustively over the checks of the presence: whatever check failed
/// (or could not be made, or was not run), correctable or not, before or after the guided retry, in a segment that allows the automatic approval or in observation, a similarity of
/// 0.99 on every frame never approves the guest and never makes the policy's shadow decision an approval. And the other way round: a perfect presence does not make up for a weak
/// similarity.
/// </summary>
public sealed class IdentityNoCompensationProofTests
{
    private readonly IdentityDecisionEngine _engine = new();

    [Fact]
    public void IdentityNoCompensation_AnyPresenceFailureWithHighSimilarity_NeverApproves()
    {
        var high = EvidenceBundles.Match(0.99, 0.99);
        Assert.True(high.AcceptPassed);
        var combinations = 0;

        foreach (var check in IdentityEvidenceBundle.PresenceChecks)
        {
            foreach (var status in new[] { IdentityCheckStatus.Failed, IdentityCheckStatus.Unavailable, IdentityCheckStatus.NotRun })
            {
                foreach (var correctable in new[] { true, false })
                {
                    foreach (var guidedRetriesUsed in new[] { 0, 1 })
                    {
                        foreach (var segment in new[] { EvidenceBundles.Allowed, EvidenceBundles.Observation })
                        {
                            var bundle = EvidenceBundles.Perfect(segment) with
                            {
                                Presence =
                                [
                                    .. EvidenceBundles.PassedPresence(livenessScore: 0.99).Select(outcome => outcome.Check == check
                                        ? outcome with { Status = status, Correctable = correctable, GuidanceCode = correctable ? IdentityGuidanceCodes.PresenceQuality : null }
                                        : outcome),
                                ],
                                Match = high,
                                GuidedRetriesUsed = guidedRetriesUsed,
                            };

                            var decision = _engine.Decide(bundle);
                            combinations++;

                            var label = $"{check} {status} correctable={correctable} retries={guidedRetriesUsed} allowed={segment.AutoApprovalAllowed}";
                            Assert.True(decision.Final != IdentityFinalDecision.AutoApproved, $"approved: {label}");
                            Assert.True(decision.Shadow != IdentityShadowDecision.Approve, $"shadow approval: {label}");
                            Assert.NotEqual(BiometricOutcome.Accept, decision.LogOutcome);
                            Assert.NotEqual(BiometricOutcome.Accept, decision.ShadowOutcome);

                            var reason = Assert.Single(decision.Reasons, entry => entry.Check == check);
                            Assert.NotEqual(VerificationReasons.Results.Passed, reason.Result);
                        }
                    }
                }
            }
        }

        Assert.Equal(IdentityEvidenceBundle.PresenceChecks.Count * 3 * 2 * 2 * 2, combinations);
    }

    [Fact]
    public void IdentityNoCompensation_APerfectPresenceDoesNotMakeUpForAWeakSimilarity()
    {
        var policy = IdentityTestPolicy.Shipped().Match;
        foreach (var (aggregate, min) in new[] { (policy.Accept - 0.0001, 0.99), (0.99, policy.MinEachFrame - 0.0001), (policy.ReviewFloor - 0.0001, policy.ReviewFloor - 0.0001) })
        {
            var bundle = EvidenceBundles.Perfect(EvidenceBundles.Allowed) with
            {
                Presence = EvidenceBundles.PassedPresence(livenessScore: 1.0),
                Match = EvidenceBundles.Match(aggregate, min),
            };

            var decision = _engine.Decide(bundle);

            Assert.Equal(IdentityFinalDecision.Review, decision.Final);
            Assert.Equal(IdentityShadowDecision.Review, decision.Shadow);
        }
    }
}
