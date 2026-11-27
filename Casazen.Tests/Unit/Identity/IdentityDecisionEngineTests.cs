using System.Reflection;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US01/T01 (NF-194, ADR-013-R18, R19, decisions D7 and D8): the engine of the versioned policy. One test per rule, in the order the engine applies them: a fault never
/// approves; correctable failures get ONE guided retry and are reviewed after it; the document, the similarity and the assurance send to a person; perfect evidence is a shadow
/// approval that becomes the approval only where the segment allows it; there is no rejection; and the engine is pure.
/// </summary>
public sealed class IdentityDecisionEngineTests
{
    private readonly IdentityDecisionEngine _engine = new();

    [Fact]
    public void IdentityDecisionEngine_IsPure_NoDependencyNoStateNoClockNoRandomness()
    {
        // AC1: one constructor, without parameters; no field at all (instance or static), so no logger, clock, random generator, database or container can hide in it.
        var type = typeof(IdentityDecisionEngine);
        var constructor = Assert.Single(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Empty(constructor.GetParameters());
        Assert.Empty(type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Empty(type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

        // The same bundle gives the same decision, from the same engine and from another one.
        foreach (var bundle in new[] { EvidenceBundles.Perfect(EvidenceBundles.Allowed), EvidenceBundles.Perfect(EvidenceBundles.Observation), EvidenceBundles.WithFailure(VerificationReasons.Checks.PresenceTiming, correctable: true) })
        {
            var first = _engine.Decide(bundle);
            var again = _engine.Decide(bundle);
            var other = new IdentityDecisionEngine().Decide(bundle);
            foreach (var decision in new[] { again, other })
            {
                Assert.Equal((first.Final, first.Shadow, first.OutcomeCode, first.Mode), (decision.Final, decision.Shadow, decision.OutcomeCode, decision.Mode));
                Assert.Equal(first.Reasons, decision.Reasons);
                Assert.Equal(first.Guidance, decision.Guidance);
            }
        }
    }

    [Fact]
    public void IdentityDecisionEngine_ModelsFault_NeverApproves()
    {
        // Proof (security, AC2, AC-NF194-2): whatever the fault, on otherwise perfect evidence in a segment that allows the approval, the decision is the review of a person, with
        // the outcome unavailable (nothing could be judged) or technical_error (a model failed or the time was over), and the policy would not approve either.
        var allowed = EvidenceBundles.Perfect(EvidenceBundles.Allowed);
        var faults = new (string Name, IdentityEvidenceBundle Bundle, string Outcome, string Processing)[]
        {
            ("policy unavailable", allowed with { PolicyState = IdentityPolicyState.Unavailable, PolicyReasonCode = IdentityPolicyReasonCodes.PolicyHashMismatch }, IdentityOutcomeCodes.Unavailable, VerificationReasons.Results.Unavailable),
            ("models not ready", allowed with { ModelsFault = IdentityModelsFault.NotReady }, IdentityOutcomeCodes.Unavailable, VerificationReasons.Results.Unavailable),
            ("model exception", allowed with { ModelsFault = IdentityModelsFault.Failed }, IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Error),
            ("timeout", allowed with { ModelsFault = IdentityModelsFault.TimedOut }, IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Timeout),
            ("liveness could not run", EvidenceBundles.WithStatus(VerificationReasons.Checks.Liveness, IdentityCheckStatus.Unavailable, EvidenceBundles.Allowed), IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Error),
            ("validation could not run", EvidenceBundles.WithStatus(VerificationReasons.Checks.PresenceContinuity, IdentityCheckStatus.Unavailable, EvidenceBundles.Allowed), IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Error),
            ("match could not run", allowed with { Match = EvidenceBundles.Match(0.99, 0.99) with { State = MatchState.Unavailable } }, IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Error),
            ("unknown fault", allowed with { ModelsFault = (IdentityModelsFault)42 }, IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Error),
            ("unknown policy state", allowed with { PolicyState = (IdentityPolicyState)42 }, IdentityOutcomeCodes.Unavailable, VerificationReasons.Results.Unavailable),
        };

        foreach (var (name, bundle, outcome, processing) in faults)
        {
            var decision = _engine.Decide(bundle);

            Assert.True(decision.Final == IdentityFinalDecision.Review, $"{name}: {decision.Final}");
            Assert.True(decision.Shadow == IdentityShadowDecision.Review, $"{name}: shadow {decision.Shadow}");
            Assert.Equal(outcome, decision.OutcomeCode);
            Assert.Equal(BiometricOutcome.Escalate, decision.LogOutcome);
            Assert.Contains(new VerificationReason(VerificationReasons.Checks.Processing, processing), decision.Reasons);
            Assert.Empty(decision.Guidance);
        }
    }

    [Fact]
    public void IdentityDecisionEngine_CorrectableFailures_GetOneGuidedRetry_AndTheSameFailuresAfterItAreReviewed()
    {
        // AC3, AC-NF194-5: the guided retry of the policy (closure.guidedRetries = 1) is offered once, with the guidance of the failures; after it the same defects go to a person.
        var first = _engine.Decide(EvidenceBundles.WithFailures(
            0,
            new CheckOutcome(VerificationReasons.Checks.PresenceTiming, IdentityCheckStatus.Failed, true, IdentityGuidanceCodes.PresenceTooShort),
            new CheckOutcome(VerificationReasons.Checks.PresenceChallenge, IdentityCheckStatus.Failed, true, IdentityGuidanceCodes.PresenceChallengeNotFollowed)));

        Assert.Equal(IdentityFinalDecision.GuidedRetry, first.Final);
        Assert.Equal(IdentityShadowDecision.Review, first.Shadow);
        Assert.False(first.ClosesTheVerification);
        Assert.Null(first.OutcomeCode);
        Assert.Equal([IdentityGuidanceCodes.PresenceTooShort, IdentityGuidanceCodes.PresenceChallengeNotFollowed], first.Guidance);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.PresenceTiming, VerificationReasons.Results.Failed), first.Reasons);

        var after = _engine.Decide(EvidenceBundles.WithFailures(
            1,
            new CheckOutcome(VerificationReasons.Checks.PresenceTiming, IdentityCheckStatus.Failed, true, IdentityGuidanceCodes.PresenceTooShort),
            new CheckOutcome(VerificationReasons.Checks.PresenceChallenge, IdentityCheckStatus.Failed, true, IdentityGuidanceCodes.PresenceChallengeNotFollowed)));

        Assert.Equal(IdentityFinalDecision.Review, after.Final);
        Assert.Equal(IdentityOutcomeCodes.Review, after.OutcomeCode);
        Assert.True(after.ClosesTheVerification);
        Assert.Empty(after.Guidance);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.PresenceTiming, VerificationReasons.Results.Failed), after.Reasons);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.PresenceChallenge, VerificationReasons.Results.Failed), after.Reasons);

        // A policy that offers no guided retry reviews at once.
        var none = _engine.Decide(EvidenceBundles.WithFailure(VerificationReasons.Checks.PresenceQuality, correctable: true) with { GuidedRetriesAllowed = 0 });
        Assert.Equal(IdentityFinalDecision.Review, none.Final);
    }

    [Fact]
    public void IdentityDecisionEngine_ACorrectableFailureNextToOneThatIsNot_IsReviewed_NeverRetried()
    {
        var mixed = _engine.Decide(EvidenceBundles.WithFailures(
            0,
            new CheckOutcome(VerificationReasons.Checks.PresenceQuality, IdentityCheckStatus.Failed, true, IdentityGuidanceCodes.PresenceQuality),
            new CheckOutcome(VerificationReasons.Checks.PresenceContinuity, IdentityCheckStatus.Failed, false, IdentityGuidanceCodes.PresenceContinuity)));
        Assert.Equal(IdentityFinalDecision.Review, mixed.Final);
        Assert.Empty(mixed.Guidance);

        // A correctable failure without any guidance code cannot guide a retry.
        var unguided = _engine.Decide(EvidenceBundles.WithFailures(0, new CheckOutcome(VerificationReasons.Checks.PresenceQuality, IdentityCheckStatus.Failed, true, null)));
        Assert.Equal(IdentityFinalDecision.Review, unguided.Final);
    }

    [Fact]
    public void IdentityDecisionEngine_AGuidedRetry_PrecedesTheDocumentTheFormatAndTheAssurance_AsInTheBrief()
    {
        // The order of the rules is the brief's: a correctable failure of the presence is the guided retry (b) before the document (c), the similarity (d) and the format and the
        // assurance (e) are judged. In Ticino nothing is kept to read the two sides again, so the coherence of the document is always unknown: "too dark" is still a retry there.
        var tooDark = new CheckOutcome(VerificationReasons.Checks.PresenceQuality, IdentityCheckStatus.Failed, true, IdentityGuidanceCodes.PresenceQuality);
        var chip = EvidenceBundles.Allowed with { RequiredAssurance = IdentityAssuranceLevels.Cryptographic };
        var cases = new (string Name, IdentityEvidenceBundle Bundle)[]
        {
            ("ticino: coherence unknown", EvidenceBundles.WithFailures(0, tooDark) with { DocumentCoherence = IdentityDocumentCoherence.Unknown }),
            ("identifying conflict", EvidenceBundles.WithFailures(0, tooDark) with { DocumentCoherence = IdentityDocumentCoherence.IdentifyingConflict }),
            ("invalid MRZ", EvidenceBundles.WithFailures(0, tooDark) with { DocumentCoherence = IdentityDocumentCoherence.MrzInvalid }),
            ("expired", EvidenceBundles.WithFailures(0, tooDark) with { DocumentCoherence = IdentityDocumentCoherence.Expired }),
            ("document not complete", EvidenceBundles.WithFailures(0, tooDark) with { DocumentComplete = false }),
            ("no format", EvidenceBundles.WithFailures(0, tooDark) with { FormatId = null }),
            ("assurance not given", EvidenceBundles.WithFailures(0, tooDark) with { ProvidedAssurance = null }),
            ("a chip required", EvidenceBundles.WithFailures(0, tooDark) with { Segment = chip }),
            ("a weak match already in the bundle", EvidenceBundles.WithFailures(0, tooDark) with { Match = EvidenceBundles.Match(0.4, 0.4) }),
        };

        foreach (var (name, bundle) in cases)
        {
            var retry = _engine.Decide(bundle);
            Assert.True(retry.Final == IdentityFinalDecision.GuidedRetry, $"{name}: {retry.Final}");
            Assert.Equal([IdentityGuidanceCodes.PresenceQuality], retry.Guidance);
            Assert.Equal(IdentityShadowDecision.Review, retry.Shadow);
            Assert.Null(retry.OutcomeCode);
            Assert.False(retry.ClosesTheVerification);

            // After the retry the same defect is a review, with the reasons of the document, the format and the assurance next to it.
            var after = _engine.Decide(bundle with { GuidedRetriesUsed = 1 });
            Assert.True(after.Final == IdentityFinalDecision.Review, $"{name} after the retry: {after.Final}");
            Assert.Equal(IdentityOutcomeCodes.Review, after.OutcomeCode);
        }
    }

    [Fact]
    public void IdentityDecisionEngine_AGuidedRetry_IsNeverAnApproval_WhateverTheRestOfTheEvidenceSays()
    {
        // Proof (security): putting the guided retry before the document, the similarity and the assurance can never let a failure of the presence through. Over every check of the
        // presence, every state of the document, the format, the assurance, the similarity and the segment, correctable or not, before and after the retry: a failed check is never an
        // approval and never a shadow approval; a guided retry is a decision that closes nothing, with no outcome code and the review as its shadow; and it is given once, for a correctable defect.
        var coherences = Enum.GetValues<IdentityDocumentCoherence>();
        var matches = new MatchSummary?[] { null, EvidenceBundles.Match(0.99, 0.99), EvidenceBundles.Match(0.5, 0.5), EvidenceBundles.Match(0.99, 0.2) };
        var segments = new SegmentResolution?[] { EvidenceBundles.Allowed, EvidenceBundles.Observation, null };
        var combinations = 0;
        var retries = 0;

        foreach (var check in IdentityEvidenceBundle.PresenceChecks)
        {
            foreach (var correctable in new[] { true, false })
            {
                foreach (var coherence in coherences)
                {
                    foreach (var complete in new[] { true, false })
                    {
                        foreach (var formatId in new string?[] { "it-cie-3.0", null })
                        {
                            foreach (var match in matches)
                            {
                                foreach (var segment in segments)
                                {
                                    foreach (var used in new[] { 0, 1 })
                                    {
                                        var bundle = EvidenceBundles.Perfect(segment) with
                                        {
                                            Presence =
                                            [
                                                .. EvidenceBundles.PassedPresence().Select(outcome => outcome.Check == check
                                                    ? outcome with { Status = IdentityCheckStatus.Failed, Correctable = correctable, GuidanceCode = correctable ? IdentityGuidanceCodes.PresenceQuality : null }
                                                    : outcome),
                                            ],
                                            DocumentCoherence = coherence,
                                            DocumentComplete = complete,
                                            FormatId = formatId,
                                            Match = match,
                                            GuidedRetriesUsed = used,
                                        };

                                        var decision = _engine.Decide(bundle);
                                        combinations++;

                                        var label = $"{check} correctable={correctable} {coherence} complete={complete} format={formatId} match={match?.Aggregate} segment={segment?.Mode} used={used}";
                                        Assert.True(decision.Final != IdentityFinalDecision.AutoApproved, $"approved: {label}");
                                        Assert.True(decision.Shadow == IdentityShadowDecision.Review, $"shadow approval: {label}");
                                        Assert.NotEqual(BiometricOutcome.Accept, decision.LogOutcome);
                                        Assert.NotEqual(BiometricOutcome.Accept, decision.ShadowOutcome);

                                        if (decision.Final == IdentityFinalDecision.GuidedRetry)
                                        {
                                            retries++;
                                            Assert.True(correctable && used == 0, $"retried a defect that is not correctable, or twice: {label}");
                                            Assert.Null(decision.OutcomeCode);
                                            Assert.False(decision.ClosesTheVerification);
                                            Assert.NotEmpty(decision.Guidance);
                                        }
                                        else
                                        {
                                            Assert.NotNull(decision.OutcomeCode);
                                            Assert.True(decision.ClosesTheVerification);
                                            Assert.Empty(decision.Guidance);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(IdentityEvidenceBundle.PresenceChecks.Count * 2 * coherences.Length * 2 * 2 * matches.Length * segments.Length * 2, combinations);

        // The enumeration reaches the retry: exactly the correctable defects of the first attempt (a quarter of the cases of the retry of one check: correctable, none used).
        Assert.Equal(combinations / 4, retries);
    }

    [Theory]
    [InlineData(IdentityDocumentCoherence.IdentifyingConflict, VerificationReasons.Results.Failed)]
    [InlineData(IdentityDocumentCoherence.MrzInvalid, VerificationReasons.Results.CheckDigitsInvalid)]
    [InlineData(IdentityDocumentCoherence.Expired, VerificationReasons.Results.Expired)]
    [InlineData(IdentityDocumentCoherence.Unknown, VerificationReasons.Results.NotRun)]
    public void IdentityDecisionEngine_ADocumentWhoseCoherenceIsNotAPass_IsReviewed_NeverRetried(IdentityDocumentCoherence coherence, string result)
    {
        var decision = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { DocumentCoherence = coherence });

        Assert.Equal(IdentityFinalDecision.Review, decision.Final);
        Assert.Equal(IdentityShadowDecision.Review, decision.Shadow);
        Assert.Equal(IdentityOutcomeCodes.Review, decision.OutcomeCode);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, result), decision.Reasons);

        // A document that is not complete is not a pass either.
        var incomplete = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { DocumentComplete = false });
        Assert.Equal(IdentityFinalDecision.Review, incomplete.Final);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.NotRun), incomplete.Reasons);
    }

    [Fact]
    public void IdentityDecisionEngine_ASimilarityJustBelowTheAcceptance_WithAPerfectPresence_IsReviewed()
    {
        var policy = IdentityTestPolicy.Shipped().Match;
        var decision = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { Match = EvidenceBundles.Match(policy.Accept - 0.001, policy.Accept - 0.001) });

        Assert.Equal(IdentityFinalDecision.Review, decision.Final);
        Assert.Equal(IdentityShadowDecision.Review, decision.Shadow);
        Assert.Equal(IdentityDecisionMode.Automatic, decision.Mode);
        var similarity = Assert.Single(decision.Reasons, reason => reason.Check == VerificationReasons.Checks.Similarity);
        Assert.Equal((VerificationReasons.Results.BetweenThresholds, policy.Accept), (similarity.Result, similarity.Threshold));
        Assert.All(decision.Reasons.Where(reason => IdentityEvidenceBundle.PresenceChecks.Contains(reason.Check)), reason => Assert.Equal(VerificationReasons.Results.Passed, reason.Result));
    }

    [Fact]
    public void IdentityDecisionEngine_ASimilarityUnderTheFloor_AWeakFrame_OrNoPortrait_IsReviewed()
    {
        var policy = IdentityTestPolicy.Shipped().Match;
        var underTheFloor = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { Match = EvidenceBundles.Match(policy.ReviewFloor - 0.05, policy.ReviewFloor - 0.05) });
        var weakFrame = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { Match = EvidenceBundles.Match(0.99, policy.MinEachFrame - 0.05) });
        var noPortrait = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { Match = EvidenceBundles.Match(0, 0) with { State = MatchState.NoPortrait } });
        var notRun = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { Match = null });

        foreach (var decision in new[] { underTheFloor, weakFrame, noPortrait, notRun })
        {
            Assert.Equal(IdentityFinalDecision.Review, decision.Final);
            Assert.Equal(IdentityShadowDecision.Review, decision.Shadow);
            Assert.Equal(IdentityOutcomeCodes.Review, decision.OutcomeCode);
        }

        Assert.Contains(new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Failed, Math.Round(policy.ReviewFloor - 0.05, 4), policy.ReviewFloor), underTheFloor.Reasons);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Failed, Math.Round(policy.MinEachFrame - 0.05, 4), policy.MinEachFrame), weakFrame.Reasons);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.NotRun), notRun.Reasons);
    }

    [Fact]
    public void IdentityDecisionEngine_NoFormat_OrAnAssuranceTheSegmentDoesNotGet_IsReviewedWithTheAssuranceLevel()
    {
        var chip = EvidenceBundles.Allowed with { RequiredAssurance = IdentityAssuranceLevels.Cryptographic };
        var cases = new (string Name, IdentityEvidenceBundle Bundle)[]
        {
            ("no format", EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { FormatId = null }),
            ("a format the channel does not admit", EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { ProvidedAssurance = null }),
            ("a chip required", EvidenceBundles.Perfect(chip)),
            ("the segment says so", EvidenceBundles.Perfect(EvidenceBundles.Observation with { ReasonCode = IdentitySegmentReasonCodes.AssuranceRequiresChip })),
            ("a level that is not one", EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { ProvidedAssurance = "photo" }),
        };

        foreach (var (name, bundle) in cases)
        {
            var decision = _engine.Decide(bundle);
            Assert.True(decision.Final == IdentityFinalDecision.Review, name);
            Assert.True(decision.Shadow == IdentityShadowDecision.Review, name);
            Assert.Contains(new VerificationReason(VerificationReasons.Checks.AssuranceLevel, VerificationReasons.Results.Failed), decision.Reasons);
        }

        Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentFormat, VerificationReasons.Results.Failed), _engine.Decide(cases[0].Bundle).Reasons);

        // A format that gives the chip satisfies a segment that requires it.
        var cryptographic = _engine.Decide(EvidenceBundles.Perfect(chip) with { ProvidedAssurance = IdentityAssuranceLevels.Cryptographic });
        Assert.Equal(IdentityFinalDecision.AutoApproved, cryptographic.Final);
    }

    [Fact]
    public void IdentityDecisionEngine_Observation_RecordsShadowOnly()
    {
        // Proof (security, AC5, AC-NF194-3): perfect evidence where the segment does not allow the automatic approval is the review of a person with the shadow approval and the
        // reason observation_mode, whose result is the first condition of the segment that failed; never an approval, whatever that condition is.
        var observation = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Observation));

        Assert.Equal(IdentityFinalDecision.Review, observation.Final);
        Assert.Equal(IdentityShadowDecision.Approve, observation.Shadow);
        Assert.Equal(IdentityDecisionMode.Observation, observation.Mode);
        Assert.Equal(IdentityOutcomeCodes.Review, observation.OutcomeCode);
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Accept), (observation.LogOutcome, observation.ShadowOutcome));
        Assert.Equal(new VerificationReason(VerificationReasons.Checks.ObservationMode, IdentitySegmentReasonCodes.SegmentObservation), observation.Reasons[^1]);

        foreach (var reason in IdentitySegmentReasonCodes.All.Where(code => code != IdentitySegmentReasonCodes.AssuranceRequiresChip))
        {
            var decision = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Observation with { ReasonCode = reason }));
            Assert.Equal(IdentityFinalDecision.Review, decision.Final);
            Assert.Equal(IdentityShadowDecision.Approve, decision.Shadow);
            Assert.Equal(new VerificationReason(VerificationReasons.Checks.ObservationMode, reason), decision.Reasons[^1]);
        }

        // No segment resolved at all is observation too.
        var unresolved = _engine.Decide(EvidenceBundles.Perfect(null));
        Assert.Equal((IdentityFinalDecision.Review, IdentityShadowDecision.Approve, IdentityDecisionMode.Observation), (unresolved.Final, unresolved.Shadow, unresolved.Mode));
    }

    [Fact]
    public void IdentityDecisionEngine_QualifiedAndAllowed_ApprovesAutomatically()
    {
        // AC6, AC-NF194-4: the approval needs the segment to allow it (qualified for the policy and the weights in force, with the global permission on).
        var decision = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed));

        Assert.Equal(IdentityFinalDecision.AutoApproved, decision.Final);
        Assert.Equal(IdentityShadowDecision.Approve, decision.Shadow);
        Assert.Equal(IdentityDecisionMode.Automatic, decision.Mode);
        Assert.Equal(IdentityOutcomeCodes.AutoApproved, decision.OutcomeCode);
        Assert.Equal((BiometricOutcome.Accept, BiometricOutcome.Accept), (decision.LogOutcome, decision.ShadowOutcome));
        Assert.Empty(decision.Guidance);
        Assert.DoesNotContain(decision.Reasons, reason => reason.Check == VerificationReasons.Checks.ObservationMode);
        Assert.All(decision.Reasons, reason => Assert.Equal(VerificationReasons.Results.Passed, reason.Result));

        // The same evidence in the same segment once the qualification no longer matches (a new policy hash or weights hash): back to observation, by construction.
        var mismatch = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed with { AutoApprovalAllowed = false, Qualified = false, ReasonCode = IdentitySegmentReasonCodes.QualificationMismatch }));
        Assert.Equal((IdentityFinalDecision.Review, IdentityShadowDecision.Approve), (mismatch.Final, mismatch.Shadow));
    }

    [Fact]
    public void IdentityDecisionEngine_TheDecisionHasNoReject_AndItsDefaultsAreTheReview()
    {
        // AC6, D8: there is no rejection in the decision types, and the default of each is the review (a decision never filled in fails closed).
        Assert.DoesNotContain(Enum.GetNames<IdentityFinalDecision>(), name => name.Contains("Reject", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Enum.GetNames<IdentityShadowDecision>(), name => name.Contains("Reject", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Review", "GuidedRetry", "AutoApproved"], Enum.GetNames<IdentityFinalDecision>());
        Assert.Equal(IdentityFinalDecision.Review, default);
        Assert.Equal(IdentityShadowDecision.Review, default);
        Assert.Equal(IdentityCheckStatus.NotRun, default);
        Assert.Equal(IdentityDocumentCoherence.Unknown, default);

        // Whatever the decision, the log never says Reject.
        foreach (var final in Enum.GetValues<IdentityFinalDecision>())
        {
            foreach (var shadow in Enum.GetValues<IdentityShadowDecision>())
            {
                var decision = new IdentityDecision(final, shadow, [], [], null, IdentityDecisionMode.Observation);
                Assert.NotEqual(BiometricOutcome.Reject, decision.LogOutcome);
                Assert.NotEqual(BiometricOutcome.Reject, decision.ShadowOutcome);
            }
        }
    }

    [Fact]
    public void IdentityDecisionEngine_AMissingCheck_OrAStatusThatIsNotOne_NeverApproves()
    {
        var withoutLiveness = EvidenceBundles.Perfect(EvidenceBundles.Allowed) with
        {
            Presence = [.. EvidenceBundles.PassedPresence().Where(check => check.Check != VerificationReasons.Checks.Liveness)],
        };
        var missing = _engine.Decide(withoutLiveness);
        Assert.Equal(IdentityFinalDecision.Review, missing.Final);
        Assert.Equal(IdentityShadowDecision.Review, missing.Shadow);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.Liveness, VerificationReasons.Results.NotRun), missing.Reasons);

        var empty = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with { Presence = [] });
        Assert.Equal(IdentityFinalDecision.Review, empty.Final);

        var strange = _engine.Decide(EvidenceBundles.WithStatus(VerificationReasons.Checks.PresenceQuality, (IdentityCheckStatus)42, EvidenceBundles.Allowed));
        Assert.NotEqual(IdentityFinalDecision.AutoApproved, strange.Final);
        Assert.Equal(IdentityShadowDecision.Review, strange.Shadow);

        // A check given twice is its worst entry; an extra check the engine does not know still has to pass.
        var twice = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with
        {
            Presence = [.. EvidenceBundles.PassedPresence(), new CheckOutcome(VerificationReasons.Checks.PresenceTiming, IdentityCheckStatus.Failed, false, IdentityGuidanceCodes.PresenceTiming)],
        });
        Assert.Equal(IdentityFinalDecision.Review, twice.Final);
        var extra = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with
        {
            Presence = [.. EvidenceBundles.PassedPresence(), new CheckOutcome("presence_future_check", IdentityCheckStatus.Failed)],
        });
        Assert.Equal(IdentityFinalDecision.Review, extra.Final);
    }

    [Fact]
    public void IdentityDecisionEngine_TheReasonsAreTheShapeOfReasonsJson_CodesAndTheNumbersOfThePolicyOnly()
    {
        var policy = IdentityTestPolicy.Shipped();
        var decision = _engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Observation));

        var json = VerificationReasons.Serialize(decision.Reasons);
        Assert.Equal(decision.Reasons, VerificationReasons.Parse(json));
        Assert.Equal(
            [.. IdentityEvidenceBundle.PresenceChecks, VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Checks.Similarity, VerificationReasons.Checks.AssuranceLevel, VerificationReasons.Checks.ObservationMode],
            decision.Reasons.Select(reason => reason.Check));
        var liveness = Assert.Single(decision.Reasons, reason => reason.Check == VerificationReasons.Checks.Liveness);
        Assert.Equal(policy.Presence.Liveness.MinScore, liveness.Threshold);
        var similarity = Assert.Single(decision.Reasons, reason => reason.Check == VerificationReasons.Checks.Similarity);
        Assert.Equal(policy.Match.Accept, similarity.Threshold);
    }

    [Fact]
    public void AddSelfHostedIdentityVerification_RegistersTheEngineOnceForTheProcess()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSelfHostedIdentityVerification();

        var registration = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IdentityDecisionEngine));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IdentityDecisionEngine>());
    }
}

/// <summary>
/// The bundles of the tests of the decision (E73/F01/US01/T01): perfect evidence with the numbers of the committed policy (an aggregate similarity of 0.93 and a weakest frame of
/// 0.91 against the acceptance of 0.85, a liveness of 0.93 against 0.7), and the same with one change. Public for the qualification tests of E74.
/// </summary>
public static class EvidenceBundles
{
    /// <summary>A segment in observation, the behaviour everywhere: a person decides.</summary>
    public static SegmentResolution Observation { get; } =
        new(null, IdentitySegmentMode.Observation, false, false, IdentityAssuranceLevels.Consistency, IdentitySegmentReasonCodes.SegmentObservation);

    /// <summary>A segment that allows the automatic approval: automatic, qualified for the policy and the weights in force, with the global permission on.</summary>
    public static SegmentResolution Allowed { get; } =
        new(Guid.Parse("7c1c3f6e-27e5-4a52-9d3c-0d9b8f1a6a10"), IdentitySegmentMode.Automatic, true, true, IdentityAssuranceLevels.Consistency, null);

    /// <summary>Every check of the presence passed, the liveness with its score and the minimum of the committed policy.</summary>
    public static IReadOnlyList<CheckOutcome> PassedPresence(double livenessScore = 0.93)
    {
        var minScore = IdentityTestPolicy.Shipped().Presence.Liveness.MinScore;
        return
        [
            .. IdentityEvidenceBundle.PresenceChecks.Select(check => check == VerificationReasons.Checks.Liveness
                ? new CheckOutcome(check, IdentityCheckStatus.Passed, Value: livenessScore, Threshold: minScore)
                : new CheckOutcome(check, IdentityCheckStatus.Passed)),
        ];
    }

    /// <summary>A comparison that was made, with the verdicts the matcher gives these numbers under the committed policy.</summary>
    public static MatchSummary Match(double aggregate, double min)
    {
        var policy = IdentityTestPolicy.Shipped().Match;
        return new MatchSummary(
            MatchState.Ok,
            aggregate,
            min,
            aggregate >= policy.Accept && min >= policy.MinEachFrame,
            aggregate >= policy.ReviewFloor && aggregate < policy.Accept,
            policy.Accept,
            policy.ReviewFloor,
            policy.MinEachFrame);
    }

    /// <summary>Perfect evidence of a CIE in a browser, in <paramref name="segment"/>, with no guided retry used.</summary>
    public static IdentityEvidenceBundle Perfect(SegmentResolution? segment)
    {
        return new IdentityEvidenceBundle(
            IdentityPolicyState.Ready,
            null,
            DocumentComplete: true,
            IdentityDocumentCoherence.Passed,
            "it-cie-3.0",
            IdentityChannels.AndroidChrome,
            IdentityAssuranceLevels.Consistency,
            PassedPresence(),
            Match(0.93, 0.91),
            IdentityModelsFault.None,
            segment,
            GuidedRetriesUsed: 0,
            GuidedRetriesAllowed: IdentityTestPolicy.Shipped().Closure.GuidedRetries);
    }

    /// <summary>Perfect evidence in a segment that allows the approval, but for <paramref name="check"/>, which failed; the match was not made after a failed presence.</summary>
    public static IdentityEvidenceBundle WithFailure(string check, bool correctable, int guidedRetriesUsed = 0, string? guidance = IdentityGuidanceCodes.PresenceQuality)
    {
        return WithFailures(guidedRetriesUsed, new CheckOutcome(check, IdentityCheckStatus.Failed, correctable, guidance));
    }

    /// <summary>Perfect evidence in a segment that allows the approval, but for <paramref name="failures"/>; the match was not made after a failed presence.</summary>
    public static IdentityEvidenceBundle WithFailures(int guidedRetriesUsed, params CheckOutcome[] failures)
    {
        var failed = failures.Select(failure => failure.Check).ToHashSet(StringComparer.Ordinal);
        return Perfect(Allowed) with
        {
            Presence = [.. PassedPresence().Where(check => !failed.Contains(check.Check)), .. failures],
            Match = null,
            GuidedRetriesUsed = guidedRetriesUsed,
        };
    }

    /// <summary>Perfect evidence in <paramref name="segment"/>, but for <paramref name="check"/>, whose status is <paramref name="status"/>.</summary>
    public static IdentityEvidenceBundle WithStatus(string check, IdentityCheckStatus status, SegmentResolution? segment)
    {
        return Perfect(segment) with
        {
            Presence = [.. PassedPresence().Select(outcome => outcome.Check == check ? outcome with { Status = status } : outcome)],
        };
    }
}
