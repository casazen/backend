using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public sealed class PresenceNegativeProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

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

    // No compensation between the thresholds: a high similarity never makes up for a low liveness.
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

        // The refusal consumed nothing: the same challenge still takes the sequence.
        var corrected = await world.SubmitAsync(session, sent, issue);
        Assert.Equal(PresenceStepStatus.Done, corrected.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, corrected.Result);
    }

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

    private static async Task<SyntheticIdentityWorld> WorldAsync(bool qualified, string? policyText = null)
    {
        var world = await SyntheticIdentityWorld.CreateAsync(policyText);
        world.Inner.Segment = qualified ? EvidenceBundles.Allowed : EvidenceBundles.Observation;
        return world;
    }

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

    private static async Task AssertNothingWasConsumedAsync(SyntheticIdentityWorld world, GuestCheckInSession session)
    {
        Assert.Empty(await world.LogRowsAsync(session));
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((0, 0, (string?)null, (DateTime?)null, (DateTime?)null), (saved.BiometricAttempts, saved.IdentityGuidedRetries, saved.IdentityLastAttemptKey, saved.IdentityClosedAt, saved.OperatorRequestedAt));
        Assert.NotNull(PresenceClosureKit.ReadCheck(saved).Presence);
        Assert.Empty(world.Inner.Alerts.Raised);
        await world.AssertNeverApprovedAsync(session, answer: null);
    }

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
