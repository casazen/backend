using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F02/US01/T01 (NF-198, NF-192, decisions D6, D7, D8, D12), the negative proofs of the presence. Each defect a guest, an attacker or a network can bring is sent through the real
/// flow, the real closure and the real pipeline of the application (validation, choice of the frames, liveness, match, the engine of the policy) over doubles of the models scripted
/// to say the best of the person where the defect leaves them a say. Each ends in a guided retry (a new recording fixes it) or in a review (nothing does), never in an approval, with the
/// reason the brief expects, in observation and in a segment qualified for the automatic approval with the global flag on. What is refused before the policy is asked (a request that is
/// not a sequence, one over the limits) writes nothing and consumes nothing. The two defects of the document are proved at the document step, which the presence needs complete.
/// </summary>
public sealed class PresenceNegativeProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ───── correctable: a guided retry, then the review of the same defect ─────

    /// <summary>Proof (security, NF-192, AC-NF192-3): the moves answered out of order are not the challenge; the first attempt is the guided retry, the next with the same defect a review.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceNegativeProof_ChallengeAnsweredOutOfOrder_IsGuidedAndThenReviewed(bool qualified)
    {
        await using var world = await WorldAsync(qualified);
        var session = await world.ReadyStayAsync();

        await AssertGuidedThenReviewedAsync(
            world,
            session,
            steps => SequenceTestFrames.Genuine(world.Policy, steps, performed: [steps[1], steps[0], .. steps.Skip(2)]),
            IdentityGuidanceCodes.PresenceChallengeNotFollowed,
            VerificationReasons.Checks.PresenceChallenge,
            qualified);
    }

    /// <summary>Proof (security, NF-192): someone else in the frame, whatever the models say of the person, is not a presence: guided, then a review with the single-face reason.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceNegativeProof_TwoFaces_AreGuidedAndThenReviewed(bool qualified)
    {
        await using var world = await WorldAsync(qualified);
        var session = await world.ReadyStayAsync();

        await AssertGuidedThenReviewedAsync(
            world,
            session,
            steps =>
            {
                var sequence = SequenceTestFrames.Genuine(world.Policy, steps);
                for (var index = 10; index < 15; index++)
                    sequence.Frames[index].Faces.Add(SequenceTestFrames.Face(sequence.Width, sequence.Height, 0, 0, centerX: 0.18, centerY: 0.55, widthShare: 0.2));

                return sequence;
            },
            IdentityGuidanceCodes.PresenceSingleFace,
            VerificationReasons.Checks.PresenceSingleFace,
            qualified);
    }

    /// <summary>Proof (security, NF-192): a sequence over the number of frames of the policy is a timing that is not plausible: guided, then a review with the timing reason. The limit is the policy's, not a number of the code.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceNegativeProof_MoreFramesThanThePolicyAllows_AreGuidedAndThenReviewed(bool qualified)
    {
        await using var world = await WorldAsync(qualified, IdentityPolicyTestFiles.Edited(("presence.sequence.maxFrames", "40")));
        var session = await world.ReadyStayAsync();

        await AssertGuidedThenReviewedAsync(
            world,
            session,
            steps => SequenceTestFrames.Genuine(world.Policy, steps),
            IdentityGuidanceCodes.PresenceTiming,
            VerificationReasons.Checks.PresenceTiming,
            qualified);
        Assert.True(51 > world.Policy.Presence.Sequence.MaxFrames);
    }

    // ───── not correctable: a review at once, whatever the models say ─────

    /// <summary>Proof (security, NF-192, AC-NF192-3): a recording longer than the time since the challenge was made before the challenge existed; no new recording explains it, so a person decides at once.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceNegativeProof_RecordingLongerThanTheTimeSinceTheChallenge_IsReviewedAtOnce(bool qualified)
    {
        await using var world = await WorldAsync(qualified);
        var session = await world.ReadyStayAsync();

        var (answer, _, _) = await world.AttemptAsync(
            session,
            steps =>
            {
                var sequence = SequenceTestFrames.Genuine(world.Policy, steps);
                sequence.ReceivedAfterIssue = TimeSpan.FromMilliseconds(sequence.DurationMs - 1000);
                return sequence;
            },
            liveness: 0.99,
            similarity: 0.99);

        await AssertReviewedAtOnceAsync(world, session, answer, VerificationReasons.Checks.PresenceTiming, qualified);
    }

    /// <summary>Proof (security, NF-192, NF-193): the face of the first half of the frames is one person's and the second half another's; a splice is never a presence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceNegativeProof_SpliceOfTwoIdentities_IsReviewedAtOnce(bool qualified)
    {
        await using var world = await WorldAsync(qualified);
        var session = await world.ReadyStayAsync();

        var (answer, _, _) = await world.AttemptAsync(
            session,
            steps =>
            {
                var sequence = SequenceTestFrames.Genuine(world.Policy, steps);
                for (var index = sequence.Frames.Count / 2; index < sequence.Frames.Count; index++)
                    sequence.Frames[index].Person = SequenceTestFrames.Person(1);

                return sequence;
            },
            liveness: 0.99,
            similarity: 0.99);

        await AssertReviewedAtOnceAsync(world, session, answer, VerificationReasons.Checks.PresenceContinuity, qualified);
    }

    /// <summary>
    /// Proof (security, NF-192, NF-193, decision D7): a screen played again to the camera has a low liveness and the face of the person on the document, so a high similarity. There is no
    /// compensation between them: the liveness fails, a person decides, and the portrait of the document is never compared with it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceNegativeProof_ScreenReplay_LowLivenessWithHighSimilarity_IsReviewedAtOnce(bool qualified)
    {
        await using var world = await WorldAsync(qualified);
        var session = await world.ReadyStayAsync();

        var (answer, _, _) = await world.AttemptAsync(session, steps => SequenceTestFrames.Genuine(world.Policy, steps), liveness: 0.2, similarity: 0.99);

        await AssertReviewedAtOnceAsync(world, session, answer, VerificationReasons.Checks.Liveness, qualified);
        Assert.True(world.Models.LivenessCalls > 0);
        Assert.False(world.Models.PortraitWasEmbedded);
        Assert.Contains(
            VerificationReasons.Parse(Assert.Single(await world.LogRowsAsync(session)).ReasonsJson),
            reason => reason.Check == VerificationReasons.Checks.Similarity && reason.Result == VerificationReasons.Results.NotRun);
    }

    /// <summary>Proof (security, NF-192): the same bytes in every frame are a picture played again, not a head that moves; the continuity fails and a person decides.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceNegativeProof_ByteIdenticalFrames_AreReviewedAtOnce(bool qualified)
    {
        await using var world = await WorldAsync(qualified);
        var session = await world.ReadyStayAsync();

        var (answer, sent, _) = await world.AttemptAsync(session, _ => SyntheticIdentityFixtures.StillFrameRepeated(world.Policy), liveness: 0.99, similarity: 0.99);

        Assert.True(sent.Count > 1);
        Assert.Single(sent.Jpegs.Select(ScriptedModels.Key).Distinct(StringComparer.Ordinal));
        await AssertReviewedAtOnceAsync(world, session, answer, VerificationReasons.Checks.PresenceContinuity, qualified);
    }

    // ───── refused before the policy is asked: nothing is written, nothing is consumed ─────

    /// <summary>Proof (security, NF-192): timestamps that do not increase are not a recording; the request is refused as an invalid sequence, the challenge is not consumed and nothing is decided.</summary>
    [Fact]
    public async Task PresenceNegativeProof_NonMonotonicTimestamps_AreRefusedAsAnInvalidSequence_AndConsumeNothing()
    {
        await using var world = await WorldAsync(qualified: false);
        var session = await world.ReadyStayAsync();
        var issue = await world.IssueAsync(session);
        var sent = world.Models.Prepare(SequenceTestFrames.Genuine(world.Policy, issue.Steps), world.Policy, liveness: 0.99);
        var timestamps = Enumerable.Range(0, sent.Count).Select(index => index * 125L).ToList();
        timestamps[20] = timestamps[19];
        var tampered = new PreparedSequence(sent.Jpegs, PreparedSequence.TimestampsOf(timestamps), sent.ReceivedAfterIssue);

        var refused = await world.SubmitAsync(session, tampered, issue);

        Assert.Equal(PresenceStepStatus.SequenceInvalid, refused.Status);
        Assert.Null(refused.Result);
        await AssertNothingWasConsumedAsync(world, session);

        // The same challenge still takes the request once it is a sequence: the refusal spent nothing of the guest (a person decides, in observation).
        var corrected = await world.SubmitAsync(session, sent, issue);
        Assert.Equal(PresenceStepStatus.Done, corrected.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, corrected.Result);
    }

    /// <summary>Proof (security, NF-192, ADR-013-R22): more frames than the hard cap of the request are refused before one is read; the challenge is not consumed and no model is asked.</summary>
    [Fact]
    public async Task PresenceNegativeProof_TooManyFrames_AreRefusedAsTooLarge_AndConsumeNothing()
    {
        await using var world = await WorldAsync(qualified: true);
        var session = await world.ReadyStayAsync();
        var issue = await world.IssueAsync(session);
        var tooMany = world.Limits.Presence.MaxFrames + 1;

        var refused = await world.Flow().SubmitPresenceSequenceAsync(
            session,
            PresenceClosureKit.Submission(SyntheticIdentityFixtures.NewKey(), issue.ChallengeToken, frames: tooMany),
            Ct);

        Assert.Equal(PresenceStepStatus.SequenceTooLarge, refused.Status);
        Assert.Null(refused.Result);
        Assert.Equal(0, world.Models.Detections);
        await AssertNothingWasConsumedAsync(world, session);
    }

    // ───── the document the presence needs complete ─────

    /// <summary>
    /// Proof (security, NF-189): the same picture sent for both sides of the card is a duplicated side; it is refused with its targeted guidance (a new capture, no attempt spent), the
    /// document stays incomplete and the presence step is never offered, so nothing can be approved on it.
    /// </summary>
    [Fact]
    public async Task PresenceNegativeProof_DuplicatedDocumentSide_IsRefusedWithItsGuidance_AndThePresenceIsNotOffered()
    {
        await using var world = await SyntheticIdentityWorld.CreateAsync();
        world.Inner.Segment = EvidenceBundles.Allowed;
        var session = await world.ConsentedStayAsync();
        var front = DocumentStepSelfHostedTests.Picture(DocumentSide.Front);
        world.DocumentZones.Fields[DocumentSide.Front] = DocumentStepSelfHostedTests.FrontFields();
        Assert.True((await world.DocumentFlow().SubmitDocumentAsync(session, new MemoryStream(front), DocumentSide.Front, Ct)).DocumentStored);

        var duplicate = await world.DocumentFlow().SubmitDocumentAsync(session, new MemoryStream(front), DocumentSide.Back, Ct);

        Assert.False(duplicate.Acceptable);
        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, duplicate.GuidanceCodes[0]);
        Assert.Equal(["front"], duplicate.SidesReceived);
        await AssertThePresenceIsNotOfferedAsync(world, session);
    }

    /// <summary>
    /// Proof (security, NF-189): a picture that is not a card (a foreign image) as the front of the document is refused with the guidance that a document was not found, nothing of it
    /// is kept, no attempt is spent, and the presence step is never offered.
    /// </summary>
    [Fact]
    public async Task PresenceNegativeProof_ForeignImageAsTheDocumentFront_IsRefusedWithItsGuidance_AndThePresenceIsNotOffered()
    {
        await using var world = await SyntheticIdentityWorld.CreateAsync();
        world.Inner.Segment = EvidenceBundles.Allowed;
        var session = await world.ConsentedStayAsync();

        var refused = await world.DocumentFlow().SubmitDocumentAsync(session, new MemoryStream(DocumentStepSelfHostedTests.PlainPicture(120)), DocumentSide.Front, Ct);

        Assert.False(refused.Acceptable);
        Assert.False(refused.DocumentStored);
        Assert.Contains(IdentityGuidanceCodes.DocumentNotFound, refused.GuidanceCodes);
        Assert.Empty(world.Inner.Storage.Objects);
        await AssertThePresenceIsNotOfferedAsync(world, session);
    }

    // ───── what every proof checks ─────

    private static async Task<SyntheticIdentityWorld> WorldAsync(bool qualified, string? policyText = null)
    {
        var world = await SyntheticIdentityWorld.CreateAsync(policyText);
        world.Inner.Segment = qualified ? EvidenceBundles.Allowed : EvidenceBundles.Observation;
        return world;
    }

    /// <summary>A defect a new recording fixes: the first attempt is the guided retry with its code, the second (the same defect) is a review with its reason and no approval.</summary>
    private static async Task AssertGuidedThenReviewedAsync(
        SyntheticIdentityWorld world,
        GuestCheckInSession session,
        Func<IReadOnlyList<string>, ScriptedSequence> script,
        string guidanceCode,
        string check,
        bool qualified)
    {
        var (first, _, _) = await world.AttemptAsync(session, script, liveness: 0.99, similarity: 0.99);

        Assert.Equal(PresenceStepStatus.Done, first.Status);
        Assert.Equal(IdentityVerificationGuestResults.Retry, first.Result);
        Assert.True(first.GuidedRetryAvailable);
        Assert.Contains(guidanceCode, first.Guidance);
        Assert.Empty(await world.LogRowsAsync(session));
        await world.AssertNeverApprovedAsync(session, first);

        var (second, _, _) = await world.AttemptAsync(session, script, liveness: 0.99, similarity: 0.99);

        await AssertReviewedAtOnceAsync(world, session, second, check, qualified);
    }

    /// <summary>A review with the check that failed, the rows that say it was never an approval (not even in shadow), and no code released.</summary>
    private static async Task AssertReviewedAtOnceAsync(SyntheticIdentityWorld world, GuestCheckInSession session, PresenceStepResult answer, string check, bool qualified)
    {
        Assert.Equal(PresenceStepStatus.Done, answer.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        Assert.False(answer.GuidedRetryAvailable);
        await world.AssertNeverApprovedAsync(session, answer);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate), (row.Outcome, row.ShadowOutcome));
        Assert.Equal(qualified ? IdentityDecisionMode.Automatic : IdentityDecisionMode.Observation, row.DecisionMode);
        Assert.Contains(VerificationReasons.Parse(row.ReasonsJson), reason => reason.Check == check && reason.Result == VerificationReasons.Results.Failed);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(IdentityOutcomeCodes.Review, saved.IdentityOutcomeCode);
        Assert.NotNull(saved.OperatorRequestedAt);
        Assert.Equal([(session.Id, VerificationAlertReasons.Review)], world.Inner.Alerts.Raised);
    }

    /// <summary>A request that was refused wrote nothing: no row, no attempt, no retry, no key kept, the challenge still there, the operators not told.</summary>
    private static async Task AssertNothingWasConsumedAsync(SyntheticIdentityWorld world, GuestCheckInSession session)
    {
        Assert.Empty(await world.LogRowsAsync(session));
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((0, 0, (string?)null, (DateTime?)null, (DateTime?)null), (saved.BiometricAttempts, saved.IdentityGuidedRetries, saved.IdentityLastAttemptKey, saved.IdentityClosedAt, saved.OperatorRequestedAt));
        Assert.NotNull(PresenceClosureKit.ReadCheck(saved).Presence);
        Assert.Empty(world.Inner.Alerts.Raised);
        await world.AssertNeverApprovedAsync(session, answer: null);
    }

    /// <summary>The document is not complete: the verification is open, no attempt was spent, no row was written, and the page is offered no presence step.</summary>
    private static async Task AssertThePresenceIsNotOfferedAsync(SyntheticIdentityWorld world, GuestCheckInSession session)
    {
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((0, (DateTime?)null, (DateTime?)null), (saved.BiometricAttempts, saved.IdentityClosedAt, saved.OperatorRequestedAt));
        Assert.Null(await world.Flow().IssuePresenceChallengeAsync(session, Ct));
        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.Equal(IdentityVerificationSteps.Document, state.Step);
        Assert.False(state.DocumentChecked);
        await world.AssertNeverApprovedAsync(session, answer: null);
    }
}
