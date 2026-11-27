using System.Globalization;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US01/T02 (NF-192, NF-194, ADR-013-R17..R19, decision D8): the closure of an attempt of the presence. The real flow, the real reader of the frames, the real policy file and
/// engine, the real registry of the formats; a double of the pipeline of E72 (<see cref="ScriptedEvidenceCollector"/>) and of the segments. One lock and one save: the log row carries the
/// decision mode, the shadow outcome, the policy version and hash, the format, the channel and the key; a fault never approves and the request for a person stays open; observation is a
/// review with the shadow approval; only a segment that allows it approves, through the success path of today.
/// </summary>
public sealed class PresenceClosureTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task PresenceClosure_ObservationWithPerfectEvidence_RowIsEscalateWithShadowAccept_AndAPersonDecides()
    {
        // AC1, AC5, AC-NF194-3: the decision is taken in one save, its row says how, and the session closes with the outcome code review.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        var key = PresenceClosureKit.NewKey();

        var answer = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(key, token), Ct);

        Assert.Equal(PresenceStepStatus.Done, answer.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        Assert.Empty(answer.Guidance);
        Assert.False(answer.GuidedRetryAvailable);
        Assert.Null(answer.LockCode);
        Assert.False(answer.Replayed);

        var row = Assert.Single(await world.LogRowsAsync(session));
        var policy = IdentityTestPolicy.Shipped();
        Assert.Equal(BiometricOutcome.Escalate, row.Outcome);
        Assert.Equal(BiometricOutcome.Accept, row.ShadowOutcome);
        Assert.Equal(IdentityDecisionMode.Observation, row.DecisionMode);
        Assert.Equal(VerificationMethod.Biometric, row.Method);
        Assert.Equal((policy.Version, IdentityPolicyTestFiles.ShippedCanonicalHash), (row.PolicyVersion, row.PolicyHash));
        Assert.Equal(("it-cie-3.0", IdentityChannels.AndroidChrome, key), (row.FormatId, row.Channel, row.IdempotencyKey));
        Assert.True(row.Provisional);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.DurationMs);
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.ObservationMode, IdentitySegmentReasonCodes.SegmentObservation), VerificationReasons.Parse(row.ReasonsJson));
        using (var thresholds = JsonDocument.Parse(row.ThresholdsJson))
        {
            Assert.Equal(policy.Version, thresholds.RootElement.GetProperty("version").GetString());
            Assert.Equal(policy.Match.Accept, thresholds.RootElement.GetProperty("match").GetProperty("accept").GetDouble());
            Assert.Equal(policy.Presence.Liveness.MinScore, thresholds.RootElement.GetProperty("liveness").GetProperty("minScore").GetDouble());
        }

        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((BiometricOutcome.Escalate, 1, IdentityOutcomeCodes.Review), (saved.BiometricOutcome, saved.BiometricAttempts, saved.IdentityOutcomeCode));
        Assert.NotNull(saved.IdentityClosedAt);
        Assert.NotNull(saved.OperatorRequestedAt);
        Assert.Equal((key, "operator"), (saved.IdentityLastAttemptKey, saved.IdentityLastAttemptResult));

        // The challenge is consumed and the perceptual hashes of the sides are gone; the kept picture of the document is deleted; the operators are told once, with review.
        var check = PresenceClosureKit.ReadCheck(saved);
        Assert.Null(check.Presence);
        Assert.Null(check.Front?.DHash);
        Assert.Empty(world.Storage.Objects);
        Assert.Equal([(session.Id, VerificationAlertReasons.Review)], world.Alerts.Raised);
        Assert.Empty(world.Alerts.Resolved);
        Assert.True(answer.State!.Closed);
        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.State.Outcome);
        world.Events.Verify(events => events.Record(It.IsAny<DomainEventType>(), It.IsAny<Guid>(), It.IsAny<object>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task PresenceClosure_QualifiedSegmentWithTheGlobalPermission_Accepts_AndTakesTheSuccessPathOfToday()
    {
        // AC-NF194-4: in a segment that allows it (qualified for the policy and the weights in force, with CheckInIdentityAutoApproval on) perfect evidence approves: the row is
        // Accept, the event is announced in the same save, and then the kept picture goes, the alerts are resolved and the lock code is released.
        await using var world = await PresenceClosureWorld.CreateAsync();
        world.Segment = EvidenceBundles.Allowed;
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);

        var answer = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);

        Assert.Equal(IdentityVerificationGuestResults.Ok, answer.Result);
        Assert.Equal("4321", answer.LockCode);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic), (row.Outcome, row.ShadowOutcome, row.DecisionMode));
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((BiometricOutcome.Accept, IdentityOutcomeCodes.AutoApproved), (saved.BiometricOutcome, saved.IdentityOutcomeCode));
        Assert.Null(saved.OperatorRequestedAt);
        world.Events.Verify(
            events => events.Record(DomainEventType.IdVerificationCompleted, session.BookingId, It.IsAny<IdVerificationCompletedPayload>(), null, session.OrgId),
            Times.Once);
        Assert.Empty(world.Storage.Objects);
        Assert.Equal([(session.OrgId, session.BookingId)], world.Alerts.Resolved);
        Assert.Empty(world.Alerts.Raised);
        world.AccessCodes.Verify(codes => codes.ReleaseToGuestAsync(session.BookingId, AccessCodeReleaseChannels.CheckInScreen, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(IdentityVerificationSteps.Done, answer.State!.Step);
        Assert.Equal(IdentityVerificationGuestResults.Ok, answer.State.Outcome);
    }

    [Fact]
    public async Task PresenceClosure_ModelFault_GoesToOperator()
    {
        // Proof (security, AC4, AC-NF194-2): an exception of a model, a pipeline that could not judge, models that are not loaded or the time of the attempt over: a person decides,
        // in a segment that would allow the approval; never an Accept row; and the guest can still ask for a person afterwards.
        var faults = new (string Name, Action<PresenceClosureWorld> Arrange, string Outcome, string Processing)[]
        {
            ("model exception", world => world.Collector.Fail(new InvalidOperationException("face model crashed")), IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Error),
            ("could not judge", world => world.Collector.Next(PresenceClosureKit.NotJudged()), IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Error),
            ("models not loaded", world => world.ModelsReady = false, IdentityOutcomeCodes.Unavailable, VerificationReasons.Results.Unavailable),
            ("time over", world =>
            {
                world.Limits.Presence.SequenceTimeoutMs = 500;
                world.Collector.WaitForTheBudget();
            }, IdentityOutcomeCodes.TechnicalError, VerificationReasons.Results.Timeout),
        };

        foreach (var (name, arrange, outcome, processing) in faults)
        {
            await using var world = await PresenceClosureWorld.CreateAsync();
            world.Segment = EvidenceBundles.Allowed;
            var session = await world.ReadyStayAsync();
            var token = await world.IssueChallengeAsync(session);
            arrange(world);

            var answer = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);

            Assert.True(answer.Result == IdentityVerificationGuestResults.Operator, name);
            Assert.Null(answer.LockCode);
            var row = Assert.Single(await world.LogRowsAsync(session));
            Assert.True(row.Outcome == BiometricOutcome.Escalate, name);
            Assert.True(row.ShadowOutcome == BiometricOutcome.Escalate, name);
            Assert.Contains(new VerificationReason(VerificationReasons.Checks.Processing, processing), VerificationReasons.Parse(row.ReasonsJson));
            var saved = await world.ReadBackAsync(session.Id);
            Assert.True(saved.IdentityOutcomeCode == outcome, name);
            Assert.NotEqual(BiometricOutcome.Accept, saved.BiometricOutcome);
            Assert.Equal([(session.Id, VerificationAlertReasons.TechnicalError)], world.Alerts.Raised);
            world.AccessCodes.Verify(codes => codes.ReleaseToGuestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            // The request for a person still works after the fault: no error, the way to a person open, the closure as it was.
            var afterwards = await world.Flow().RequestOperatorAsync(session, Ct);
            Assert.True(afterwards.OperatorPathOpen);
            Assert.Equal(IdentityVerificationGuestResults.Operator, afterwards.Result);
            Assert.Equal(outcome, (await world.ReadBackAsync(session.Id)).IdentityOutcomeCode);
        }
    }

    [Fact]
    public async Task PresenceClosure_APolicyThatIsNotPinned_GoesToOperator_AsUnavailable()
    {
        await using var world = await PresenceClosureWorld.CreateAsync(pinThePolicy: false);
        world.Segment = EvidenceBundles.Allowed;
        var session = await world.ReadyStayAsync();
        var token = await world.StoreChallengeAsync(session);

        var answer = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);

        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, (string?)null, (string?)null, "{}"), (row.Outcome, row.PolicyVersion, row.PolicyHash, row.ThresholdsJson));
        Assert.Equal(IdentityOutcomeCodes.Unavailable, (await world.ReadBackAsync(session.Id)).IdentityOutcomeCode);
        Assert.Equal(0, world.Collector.Calls);
    }

    [Fact]
    public async Task PresenceClosure_ChallengeOfAnotherSession_IsRefused()
    {
        // Proof (security, isolation): the proof of a challenge is bound to its session: sent to another session it is no challenge there, and nothing of that session moves.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var left = await world.ReadyStayAsync();
        var right = await world.ReadyStayAsync();
        var leftToken = await world.IssueChallengeAsync(left);
        await world.IssueChallengeAsync(right);
        var before = (await world.ReadBackAsync(right.Id)).DocumentCheckJson;

        var answer = await world.Flow().SubmitPresenceSequenceAsync(right, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), leftToken), Ct);

        Assert.Equal(PresenceStepStatus.ChallengeRequired, answer.Status);
        Assert.Null(answer.Result);
        Assert.Empty(await world.LogRowsAsync(right));
        var after = await world.ReadBackAsync(right.Id);
        Assert.Equal(before, after.DocumentCheckJson);
        Assert.Equal((0, 0, (string?)null), (after.BiometricAttempts, after.IdentityGuidedRetries, after.IdentityLastAttemptKey));
        Assert.Equal(0, world.Collector.Calls);

        // Its own session still takes it.
        var own = await world.Flow().SubmitPresenceSequenceAsync(left, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), leftToken), Ct);
        Assert.Equal(PresenceStepStatus.Done, own.Status);
    }

    [Fact]
    public async Task PresenceClosure_AnExpiredChallenge_IsRefused_AndNothingIsConsumed()
    {
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        world.Clock.Advance(TimeSpan.FromSeconds(IdentityTestPolicy.Shipped().Presence.Challenge.LifetimeSeconds));
        var before = (await world.ReadBackAsync(session.Id)).DocumentCheckJson;

        var answer = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);

        Assert.Equal(PresenceStepStatus.ChallengeExpired, answer.Status);
        Assert.Equal(before, (await world.ReadBackAsync(session.Id)).DocumentCheckJson);
        Assert.Empty(await world.LogRowsAsync(session));
        Assert.Equal(0, world.Collector.Calls);
    }

    [Fact]
    public async Task PresenceClosure_CorrectableDefects_GetOneGuidedRetry_AndTheSameDefectsAfterItAreReviewed()
    {
        // AC-NF194-5: the guided retry consumes the challenge, counts the retry and keeps the answer of its key, without a log row and without closing the verification; the next
        // attempt (a new challenge, a new key) with the same defects is reviewed by a person with its reasons.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        world.Collector.Next(PresenceClosureKit.CorrectableFailure(IdentityGuidanceCodes.PresenceTooShort));
        var firstKey = PresenceClosureKit.NewKey();

        var retry = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(firstKey, await world.IssueChallengeAsync(session)), Ct);

        Assert.Equal(IdentityVerificationGuestResults.Retry, retry.Result);
        Assert.Equal([IdentityGuidanceCodes.PresenceTooShort], retry.Guidance);
        Assert.True(retry.GuidedRetryAvailable);
        Assert.Empty(await world.LogRowsAsync(session));
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal((1, 0, (DateTime?)null), (saved.IdentityGuidedRetries, saved.BiometricAttempts, saved.IdentityClosedAt));
        Assert.Equal((firstKey, "retry:presence_too_short"), (saved.IdentityLastAttemptKey, saved.IdentityLastAttemptResult));
        Assert.Null(PresenceClosureKit.ReadCheck(saved).Presence);
        Assert.NotEmpty(world.Storage.Objects);
        Assert.Empty(world.Alerts.Raised);
        Assert.Equal(IdentityVerificationSteps.Presence, retry.State!.Step);
        Assert.Equal(IdentityVerificationGuestResults.Retry, retry.State.Result);
        Assert.False(retry.State.GuidedRetryAvailable);

        // The proof of the consumed challenge does not open a second attempt.
        var stale = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), null), Ct);
        Assert.Equal(PresenceStepStatus.ChallengeRequired, stale.Status);

        world.Collector.Next(PresenceClosureKit.CorrectableFailure(IdentityGuidanceCodes.PresenceTooShort));
        var second = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), await world.IssueChallengeAsync(session)), Ct);

        Assert.Equal(IdentityVerificationGuestResults.Operator, second.Result);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate), (row.Outcome, row.ShadowOutcome));
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.PresenceTiming, VerificationReasons.Results.Failed), VerificationReasons.Parse(row.ReasonsJson));
        var closed = await world.ReadBackAsync(session.Id);
        Assert.Equal((IdentityOutcomeCodes.Review, 1), (closed.IdentityOutcomeCode, closed.IdentityGuidedRetries));
    }

    [Fact]
    public async Task PresenceClosure_TooDarkInTicino_GetsTheGuidedRetry_AndTheNextAttemptIsReviewedByAPerson()
    {
        // Where nothing is kept (Ticino) the coherence of the two sides stays unknown, so a person decides every attempt there; but a recording that a new one can fix is still the guided
        // retry, as in the brief (rule b comes before the document, rule c): the engine never lets the unknown coherence take the retry away.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync(keepsNoDocument: true);
        var tooDark = new PresenceEvidence(
            new SequenceValidationReport(
                false,
                [new SequenceFailure(IdentityGuidanceCodes.PresenceQuality, VerificationReasons.Checks.PresenceQuality, Correctable: true)],
                [],
                []),
            null,
            null,
            null);
        world.Collector.Next(tooDark);

        var first = await world.Flow().SubmitPresenceSequenceAsync(session, TicinoSubmission(PresenceClosureKit.NewKey(), await world.IssueChallengeAsync(session)), Ct);

        Assert.Equal((PresenceStepStatus.Done, IdentityVerificationGuestResults.Retry, true), (first.Status, first.Result, first.GuidedRetryAvailable));
        Assert.Equal([IdentityGuidanceCodes.PresenceQuality], first.Guidance);
        Assert.Empty(await world.LogRowsAsync(session));
        var afterTheRetry = await world.ReadBackAsync(session.Id);
        Assert.Equal((1, 0, (DateTime?)null), (afterTheRetry.IdentityGuidedRetries, afterTheRetry.BiometricAttempts, afterTheRetry.IdentityClosedAt));
        Assert.Empty(world.Alerts.Raised);

        // The new recording is perfect, and the coherence cannot be told: a person decides, with the reason of the document.
        var second = await world.Flow().SubmitPresenceSequenceAsync(session, TicinoSubmission(PresenceClosureKit.NewKey(), await world.IssueChallengeAsync(session)), Ct);

        Assert.Equal(IdentityVerificationGuestResults.Operator, second.Result);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate), (row.Outcome, row.ShadowOutcome));
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.NotRun), VerificationReasons.Parse(row.ReasonsJson));
    }

    /// <summary>The attempt of a property that keeps no document: the front and the back come again with the frames.</summary>
    private static PresenceSubmission TicinoSubmission(string key, string token) =>
        PresenceClosureKit.Submission(
            key,
            token,
            documentPhoto: new MemoryStream(PresenceClosureKit.FrontPicture),
            documentPhotoBack: new MemoryStream(PresenceClosureKit.BackPicture));

    [Theory]
    [InlineData("closed", PresenceStepStatus.AlreadyClosed)]
    [InlineData("not opened", PresenceStepStatus.ChallengeRequired)]
    [InlineData("no challenge", PresenceStepStatus.ChallengeRequired)]
    [InlineData("consent text changed", PresenceStepStatus.ConsentRequired)]
    [InlineData("no document", PresenceStepStatus.DocumentRequired)]
    [InlineData("kept picture gone", PresenceStepStatus.DocumentRequired)]
    [InlineData("frames not a sequence", PresenceStepStatus.SequenceInvalid)]
    [InlineData("too many frames", PresenceStepStatus.SequenceTooLarge)]
    [InlineData("no key", PresenceStepStatus.IdempotencyKeyRequired)]
    public async Task PresenceClosure_ARequestThatIsNotRun_AnswersItsStatus_AndWritesNothing(string missing, PresenceStepStatus status)
    {
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = missing == "no challenge" ? null : await world.IssueChallengeAsync(session);
        var submission = PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token);
        switch (missing)
        {
            case "closed":
                session.IdentityClosedAt = PresenceClosureKit.Now.UtcDateTime;
                session.IdentityOutcomeCode = IdentityOutcomeCodes.Review;
                break;
            case "not opened":
                session.IdentityOpenedAt = null;
                break;
            case "consent text changed":
                world.Registry.Setup(registry => registry.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent))
                    .Returns(new CheckInLegalVersion(CheckInLegalTextKinds.BiometricConsent, "TEST-biometric-v1", false, new string('b', 64)));
                break;
            case "no document":
                session.DocumentCheckJson = null;
                break;
            case "kept picture gone":
                world.Storage.Objects.Clear();
                break;
            case "frames not a sequence":
                submission = submission with { TimestampsJson = "[0, 0, 125, 250]" };
                break;
            case "too many frames":
                world.Limits.Presence.MaxFrames = 2;
                break;
            case "no key":
                submission = submission with { IdempotencyKey = "short" };
                break;
        }

        await world.Db.SaveChangesAsync(Ct);
        var before = await world.ReadBackAsync(session.Id);

        var answer = await world.Flow().SubmitPresenceSequenceAsync(session, submission, Ct);

        Assert.Equal(status, answer.Status);
        Assert.Null(answer.Result);
        Assert.Empty(await world.LogRowsAsync(session));
        var after = await world.ReadBackAsync(session.Id);
        Assert.Equal(
            (before.DocumentCheckJson, before.IdentityGuidedRetries, before.BiometricAttempts, before.IdentityLastAttemptKey, before.OperatorRequestedAt),
            (after.DocumentCheckJson, after.IdentityGuidedRetries, after.BiometricAttempts, after.IdentityLastAttemptKey, after.OperatorRequestedAt));
        Assert.Equal(0, world.Collector.Calls);
        Assert.Empty(world.Alerts.Raised);
        if (status == PresenceStepStatus.AlreadyClosed)
            Assert.True(answer.State!.Closed);
    }

    [Fact]
    public async Task PresenceClosure_APropertyThatKeepsNoDocument_TakesThePicturesAgain_AndRefusesOthers()
    {
        // Ticino (ADR-013-R6): nothing is kept, the front and the back come again with the frames, recognised by their SHA-256; missing is 400, another picture 409.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync(keepsNoDocument: true);
        var token = await world.IssueChallengeAsync(session);

        var missing = await world.Flow().SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);
        Assert.Equal(PresenceStepStatus.DocumentPhotoRequired, missing.Status);

        var withoutBack = await world.Flow().SubmitPresenceSequenceAsync(
            session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token, documentPhoto: new MemoryStream(PresenceClosureKit.FrontPicture)), Ct);
        Assert.Equal(PresenceStepStatus.DocumentPhotoRequired, withoutBack.Status);

        var other = await world.Flow().SubmitPresenceSequenceAsync(
            session,
            PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token, documentPhoto: new MemoryStream([1, 2, 3]), documentPhotoBack: new MemoryStream(PresenceClosureKit.BackPicture)),
            Ct);
        Assert.Equal(PresenceStepStatus.DocumentPhotoMismatch, other.Status);
        Assert.Equal(0, world.Collector.Calls);

        var answered = await world.Flow().SubmitPresenceSequenceAsync(
            session,
            PresenceClosureKit.Submission(
                PresenceClosureKit.NewKey(),
                token,
                documentPhoto: new MemoryStream(PresenceClosureKit.FrontPicture),
                documentPhotoBack: new MemoryStream(PresenceClosureKit.BackPicture)),
            Ct);

        Assert.Equal(PresenceStepStatus.Done, answered.Status);
        Assert.Equal(1, world.Collector.Calls);
        Assert.Empty(world.Storage.Objects);

        // The coherence of the sides could not be checked (nothing kept to read again): never a pass, a person reviews it.
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.NotRun), VerificationReasons.Parse(row.ReasonsJson));
    }

    [Fact]
    public async Task PresenceClosure_TheFramesAndThePictureOfTheDocument_AreZeroedAfterTheRequest_AndTheLogsHoldNoNumberOfAPerson()
    {
        // Proof (privacy, AC7, ADR-013-R2): the sequence handed to the pipeline is disposed (every JPEG zeroed) and the decrypted picture of the document zeroed when the request
        // ends; the log lines of the flow carry the session, codes and counts, never a similarity, a liveness score or a picture.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        world.Collector.Next(PresenceClosureKit.Perfect(similarity: 0.9137));
        var logger = new CapturingLogger<IdentityVerificationFlowService>();

        await world.Flow(logger: logger).SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);

        var sequence = Assert.Single(world.Collector.Sequences);
        Assert.NotEmpty(sequence.Frames);
        Assert.All(sequence.Frames, frame => Assert.All(frame.Jpeg, value => Assert.Equal(0, value)));
        var document = Assert.Single(world.Collector.Documents);
        Assert.All(document, value => Assert.Equal(0, value));

        var output = logger.AllOutput;
        Assert.NotEmpty(logger.Entries);
        Assert.DoesNotContain("0.9137", output, StringComparison.Ordinal);
        Assert.DoesNotContain("0,9137", output, StringComparison.Ordinal);
        Assert.DoesNotContain("0.95", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Bianchi", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(PresenceClosureKit.FrontPicture), output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pipeline")]
    [InlineData("deletion")]
    public async Task PresenceClosure_AnExceptionIsLoggedByItsTypeOnly_NeverByItsTextNorTheNameOfTheGuest(string failing)
    {
        // Proof (privacy, ADR-013-R2): what a model or the storage throws may carry what it was reading, a name, a number of a document, a score. The log of the flow names the session and the TYPE
        // of the exception, nothing of its text, of the guest or of a number of a person: when the pipeline throws (the attempt goes to a person) and when the deletion of the kept picture does.
        await using var world = await PresenceClosureWorld.CreateAsync();
        var session = await world.ReadyStayAsync();
        var token = await world.IssueChallengeAsync(session);
        const string secret = "the face of Anna Bianchi, document CA00000AA, similarity 0.8123, crashed";
        if (failing == "pipeline")
            world.Collector.Fail(new InvalidOperationException(secret));
        else
            world.Storage.FailNextDelete = new InvalidOperationException(secret);

        var logger = new CapturingLogger<IdentityVerificationFlowService>();
        var request = world.Flow(logger: logger).SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), token), Ct);
        if (failing == "pipeline")
            Assert.Equal(IdentityVerificationGuestResults.Operator, (await request).Result);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => request);

        var output = logger.AllOutput;
        Assert.Contains(nameof(InvalidOperationException), output, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "crashed", "Anna", "Bianchi", "CA00000AA", "0.8123", "0,8123", "the face of" })
            Assert.DoesNotContain(forbidden, output, StringComparison.Ordinal);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public void PresenceEvidence_TheChecksOfTheDecision_AreTheValidationTheChoiceAndTheLiveness()
    {
        var policy = IdentityTestPolicy.Shipped();

        var perfect = PresenceClosureKit.Perfect().Checks(policy);
        Assert.Equal(IdentityEvidenceBundle.PresenceChecks, perfect.Select(check => check.Check));
        Assert.All(perfect, check => Assert.Equal(IdentityCheckStatus.Passed, check.Status));
        var liveness = Assert.Single(perfect, check => check.Check == VerificationReasons.Checks.Liveness);
        Assert.Equal((0.93, policy.Presence.Liveness.MinScore), (liveness.Value, liveness.Threshold));
        Assert.Equal(IdentityModelsFault.None, PresenceClosureKit.Perfect().ModelsFault);

        // A failure of the validation: its check failed with its correctability and its code, the others passed, the liveness not run.
        var mixed = new PresenceEvidence(
            new SequenceValidationReport(
                false,
                [
                    new SequenceFailure(IdentityGuidanceCodes.PresenceTooShort, VerificationReasons.Checks.PresenceTiming, Correctable: true),
                    new SequenceFailure(IdentityGuidanceCodes.PresenceTiming, VerificationReasons.Checks.PresenceTiming, Correctable: false),
                ],
                [],
                []),
            null,
            null,
            null).Checks(policy);
        var timing = Assert.Single(mixed, check => check.Check == VerificationReasons.Checks.PresenceTiming);
        Assert.Equal((IdentityCheckStatus.Failed, false, IdentityGuidanceCodes.PresenceTooShort), (timing.Status, timing.Correctable, timing.GuidanceCode));
        Assert.Equal(IdentityCheckStatus.NotRun, Assert.Single(mixed, check => check.Check == VerificationReasons.Checks.Liveness).Status);
        Assert.Equal(IdentityCheckStatus.Passed, Assert.Single(mixed, check => check.Check == VerificationReasons.Checks.PresenceContinuity).Status);

        // The frames could not be chosen: presence_quality, correctable.
        var notChosen = new PresenceEvidence(
            PresenceClosureKit.ValidSequence(),
            new SequenceFailure(IdentityGuidanceCodes.PresenceQuality, VerificationReasons.Checks.PresenceQuality, Correctable: true),
            null,
            null).Checks(policy);
        Assert.Equal(
            (IdentityCheckStatus.Failed, true),
            (Assert.Single(notChosen, check => check.Check == VerificationReasons.Checks.PresenceQuality).Status,
                Assert.Single(notChosen, check => check.Check == VerificationReasons.Checks.PresenceQuality).Correctable));

        // A sequence that could not be judged, or a liveness or a match that could not be made, is a fault: never a pass.
        Assert.All(PresenceClosureKit.NotJudged().Checks(policy).Take(5), check => Assert.Equal(IdentityCheckStatus.Unavailable, check.Status));
        Assert.Equal(IdentityModelsFault.Failed, PresenceClosureKit.NotJudged().ModelsFault);
        Assert.Equal(IdentityModelsFault.Failed, (PresenceClosureKit.Perfect() with { Liveness = LivenessReport.NotAvailable(), Match = null }).ModelsFault);
        Assert.Equal(IdentityModelsFault.Failed, (PresenceClosureKit.Perfect() with { Match = MatchReport.NotAvailable() }).ModelsFault);
        var failedLiveness = Assert.Single(PresenceClosureKit.LivenessFailed().Checks(policy), check => check.Check == VerificationReasons.Checks.Liveness);
        Assert.Equal((IdentityCheckStatus.Failed, false), (failedLiveness.Status, failedLiveness.Correctable));
    }

    [Fact]
    public void PresenceClosure_TheAnswerKeptForTheReplay_FitsItsColumn_AndReadsBack()
    {
        Assert.Equal("operator", IdentityVerificationFlowService.EncodeAnswer("operator", []));
        Assert.Equal("retry:presence_too_short,presence_quality", IdentityVerificationFlowService.EncodeAnswer("retry", ["presence_too_short", "presence_quality"]));

        // Every code of the presence at once does not fit in 80 characters: the first ones are kept, and that is what the guest is told and what a replay answers.
        string[] all =
        [
            IdentityGuidanceCodes.PresenceTooShort, IdentityGuidanceCodes.PresenceTiming, IdentityGuidanceCodes.PresenceSingleFace, IdentityGuidanceCodes.PresenceQuality,
            IdentityGuidanceCodes.PresenceChallengeNotFollowed,
        ];
        var kept = IdentityVerificationFlowService.FitGuidance("retry", all);
        Assert.InRange(IdentityVerificationFlowService.EncodeAnswer("retry", kept).Length, 1, IdentityVerificationFlowService.LastAttemptResultMaxLength);
        Assert.Equal(all.Take(kept.Count), kept);
        Assert.True(kept.Count >= 2);

        Assert.True(IdentityVerificationFlowService.TryReadAnswer(IdentityVerificationFlowService.EncodeAnswer("retry", kept), out var result, out var guidance));
        Assert.Equal("retry", result);
        Assert.Equal(kept, guidance);
        Assert.True(IdentityVerificationFlowService.TryReadAnswer("ok", out var ok, out var none));
        Assert.Equal("ok", ok);
        Assert.Empty(none);
        Assert.False(IdentityVerificationFlowService.TryReadAnswer("approved", out _, out _));
        Assert.False(IdentityVerificationFlowService.TryReadAnswer(null, out _, out _));
    }

    [Fact]
    public async Task PresenceClosure_TheApplicationWiresTheClosure_WithTheServicesItNeeds()
    {
        // The closure runs only when the flow holds the reader, the pipeline, the segments, the engine and the policy: a service that stopped being registered would close the presence
        // in silence. Built with the registrations of the application and doubles only for what it registers elsewhere (the models, the database, the flags, the clock).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<BiometricModelOptions>>(Options.Create(new BiometricModelOptions()));
        services.AddSingleton(Mock.Of<IFaceDetector>());
        services.AddSingleton(Mock.Of<IFaceEmbedder>());
        services.AddSingleton(Mock.Of<IPageTextReader>());
        services.AddSingleton<IBiometricModelStore>(new FakeBiometricModelStore());
        services.AddSingleton<IFeatureFlags>(new TestFeatureFlags());
        services.AddSingleton(TimeProvider.System);

        // E73/F01/US02/T01: the store of the evidence of the reviewer the flow takes writes to the private bucket with Data Protection.
        services.AddSingleton(Mock.Of<IFileStorage>());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped(_ => Mock.Of<IAntiSpoofingService>());
        services.AddScoped(_ => Mock.Of<IMrzExtractionService>());
        services.AddScoped(_ => Mock.Of<IDocumentOcrService>());
        var databaseName = $"presence-registration-{Guid.NewGuid()}";
        services.AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options));
        services.AddSelfHostedIdentityVerification();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var flow = ActivatorUtilities.CreateInstance<IdentityVerificationFlowService>(
            scope.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            Mock.Of<IIdentityVerificationAgent>(),
            Mock.Of<IVerificationOperatorAlerts>(),
            Mock.Of<IAccessCodeService>(),
            Mock.Of<IEmailQueue>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            Mock.Of<IStayGuestService>(),
            Mock.Of<IFileStorage>(),
            new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            NullLogger<IdentityVerificationFlowService>.Instance);

        Assert.True(flow.PresenceClosureWired);
        Assert.IsType<PresenceEvidenceCollector>(scope.ServiceProvider.GetRequiredService<IPresenceEvidenceCollector>());
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPresenceEvidenceCollector)).Lifetime);

        // Built by hand without them (a tool, a test of another task), the closure is not there: nothing runs, nothing is written.
        var bare = new IdentityVerificationFlowService(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            Mock.Of<IIdentityVerificationAgent>(),
            Mock.Of<IVerificationOperatorAlerts>(),
            Mock.Of<IAccessCodeService>(),
            Mock.Of<IEmailQueue>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            Mock.Of<IStayGuestService>(),
            Mock.Of<IFileStorage>(),
            new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            NullLogger<IdentityVerificationFlowService>.Instance,
            featureFlags: new TestFeatureFlags(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted));
        Assert.False(bare.PresenceClosureWired);
        var answer = await bare.SubmitPresenceSequenceAsync(new GuestCheckInSession(), PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), "proof"), Ct);
        Assert.Equal(PresenceStepStatus.SessionUnavailable, answer.Status);
    }
}

/// <summary>The pieces of the tests of the closure of the presence (E73/F01/US01/T02): the policy, the frames, the submissions and the evidence of the pipeline.</summary>
internal static class PresenceClosureKit
{
    public static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The front of the document as it was read and kept (any bytes: the pipeline is a double and never decodes it).</summary>
    public static readonly byte[] FrontPicture = Encoding.ASCII.GetBytes("TEST-DOCUMENT-FRONT-PICTURE");

    /// <summary>The back of the document as it was read.</summary>
    public static readonly byte[] BackPicture = Encoding.ASCII.GetBytes("TEST-DOCUMENT-BACK-PICTURE");

    public static string NewKey() => Guid.NewGuid().ToString();

    /// <summary>The committed policy file, written in <paramref name="directory"/> and pinned (or pinned to another hash).</summary>
    public static IdentityPolicyProvider Policy(string directory, bool pinned = true)
    {
        const string file = "identity-policy-of-the-test.json";
        File.WriteAllText(Path.Combine(directory, file), IdentityPolicyTestFiles.ShippedText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var pin = pinned ? IdentityPolicyTestFiles.ShippedCanonicalHash : new string('0', 64);
        return new IdentityPolicyProvider(
            Options.Create(new IdentityVerificationOptions { PolicyFile = file, PolicySha256 = pin }),
            NullLogger<IdentityPolicyProvider>.Instance,
            directory);
    }

    /// <summary>The registry of the formats the application ships (the CIE).</summary>
    public static DocumentFormatRegistry Formats() =>
        new(Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, System.AppContext.BaseDirectory);

    /// <summary><paramref name="count"/> small JPEG frames of noise (generated: no picture of anybody) and their timestamps, one every 125 ms from 0.</summary>
    public static (List<PresenceFrameUpload> Frames, string Timestamps) Frames(int count = 4, int seed = 1)
    {
        var frames = new List<PresenceFrameUpload>(count);
        for (var index = 0; index < count; index++)
        {
            var bytes = TestJpeg.Noise(32, 24, seed + index);
            frames.Add(new PresenceFrameUpload(new MemoryStream(bytes, writable: false), bytes.Length, "image/jpeg"));
        }

        var timestamps = "[" + string.Join(',', Enumerable.Range(0, count).Select(index => (index * 125).ToString(CultureInfo.InvariantCulture))) + "]";
        return (frames, timestamps);
    }

    public static PresenceSubmission Submission(string? key, string? token, int frames = 4, int seed = 1, Stream? documentPhoto = null, Stream? documentPhotoBack = null)
    {
        var (parts, timestamps) = Frames(frames, seed);
        return new PresenceSubmission(key, token, timestamps, parts, documentPhoto, documentPhotoBack);
    }

    /// <summary>A sequence that passed every check of the validation.</summary>
    public static SequenceValidationReport ValidSequence() => new(true, [], [], []);

    /// <summary>Everything passed: the sequence, the liveness of the chosen frames (0.95 and 0.93) and the match (<paramref name="similarity"/>, the weakest frame 0.01 below).</summary>
    public static PresenceEvidence Perfect(double similarity = 0.93)
    {
        var match = IdentityTestPolicy.Shipped().Match;
        var weakest = similarity - 0.01;
        return new PresenceEvidence(
            ValidSequence(),
            null,
            new LivenessReport(LivenessState.Passed, [new FrameLiveness(0, 0.95), new FrameLiveness(3, 0.93)]),
            new MatchReport(
                MatchState.Ok,
                similarity,
                weakest,
                match.Frames,
                similarity >= match.Accept && weakest >= match.MinEachFrame,
                similarity >= match.ReviewFloor && similarity < match.Accept));
    }

    /// <summary>A recording a new one can fix (the timing): the guided retry.</summary>
    public static PresenceEvidence CorrectableFailure(string guidance) =>
        new(new SequenceValidationReport(false, [new SequenceFailure(guidance, VerificationReasons.Checks.PresenceTiming, Correctable: true)], [], []), null, null, null);

    /// <summary>A sequence that is not live: never correctable.</summary>
    public static PresenceEvidence LivenessFailed() =>
        new(ValidSequence(), null, new LivenessReport(LivenessState.Failed, [new FrameLiveness(0, 0.2)]), null);

    /// <summary>A sequence the pipeline could not judge (a model not there, a failure, its own time over).</summary>
    public static PresenceEvidence NotJudged() => new(SequenceValidationReport.NotAvailable(), null, null, null);

    /// <summary>The checks of the document of the session.</summary>
    public static StoredDocumentCheck ReadCheck(GuestCheckInSession session) =>
        JsonSerializer.Deserialize<StoredDocumentCheck>(session.DocumentCheckJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>Every check of the coherence the CIE requires (the ones the report carries), passed: names and results only.</summary>
    public static IReadOnlyList<DocumentCoherenceCheck> PassedCoherence() =>
    [
        new(DocumentFormatChecks.MrzCheckDigits, DocumentCoherenceResults.Passed),
        new(DocumentFormatChecks.Expiry, DocumentCoherenceResults.Passed, ZonedFieldNames.ExpiryDate),
        new(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Passed, ZonedFieldNames.Surname),
        new(DocumentFormatChecks.FieldCoherence, DocumentCoherenceResults.Passed, ZonedFieldNames.BirthDate),
        new(DocumentFormatChecks.BookingConsistency, DocumentCoherenceResults.Passed, DocumentCoherenceFields.Names),
    ];
}

/// <summary>
/// A double of the pipeline of E72 (<see cref="IPresenceEvidenceCollector"/>): it answers the evidence the test scripted (perfect by default), fails, or waits for the time of the attempt
/// to be over. It keeps the sequences and the pictures of the document it was handed, the very objects of the flow, to prove they are zeroed when the request ends.
/// </summary>
internal sealed class ScriptedEvidenceCollector : IPresenceEvidenceCollector
{
    private readonly Queue<Func<CancellationToken, Task<PresenceEvidence>>> _answers = new();
    private int _calls;

    public int Calls => _calls;

    public List<FrameSequence> Sequences { get; } = [];

    public List<byte[]> Documents { get; } = [];

    public PresenceEvidence Default { get; set; } = PresenceClosureKit.Perfect();

    /// <summary>Runs, awaited, when the pipeline is asked and before it answers: what another request does while this one is in the middle of its work (a twin that writes its row).</summary>
    public Func<Task>? OnCollect { get; set; }

    public void Next(PresenceEvidence evidence) => _answers.Enqueue(_ => Task.FromResult(evidence));

    public void Fail(Exception error) => _answers.Enqueue(_ => Task.FromException<PresenceEvidence>(error));

    public void WaitForTheBudget() => _answers.Enqueue(async ct =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("The time of the attempt should have been over.");
    });

    public async Task<PresenceEvidence> CollectAsync(FrameSequence sequence, StoredPresenceChallenge challenge, byte[] documentFront, IdentityDecisionPolicy policy, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (Sequences)
        {
            Sequences.Add(sequence);
            Documents.Add(documentFront);
        }

        if (OnCollect is { } during)
            await during();

        return _answers.TryDequeue(out var next) ? await next(ct) : Default;
    }
}

/// <summary>The alerts to the operators, raised and resolved, in order.</summary>
internal sealed class RecordingOperatorAlerts : IVerificationOperatorAlerts
{
    public List<(Guid SessionId, string Reason)> Raised { get; } = [];

    public List<(Guid OrgId, Guid BookingId)> Resolved { get; } = [];

    /// <summary>The failure of the next alert (a push that is down, a database that refuses): nothing is recorded for it, the way a call that threw told nobody.</summary>
    public Exception? FailNextRaise { get; set; }

    /// <summary>The token each alert was raised with: the operators are told by the effects of a decision, which no request that drops may cancel.</summary>
    public List<CancellationToken> RaisedWith { get; } = [];

    public Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct)
    {
        if (FailNextRaise is { } failure)
        {
            FailNextRaise = null;
            return Task.FromException(failure);
        }

        lock (Raised)
        {
            Raised.Add((sessionId, reasonCode));
            RaisedWith.Add(ct);
        }

        return Task.CompletedTask;
    }

    public Task ResolveAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        lock (Resolved)
            Resolved.Add((orgId, bookingId));

        return Task.CompletedTask;
    }
}

/// <summary>A private bucket in memory.</summary>
internal sealed class PresenceMemoryStorage : IFileStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>The failure of the next deletion (a storage that is down): the object stays where it was.</summary>
    public Exception? FailNextDelete { get; set; }

    public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        lock (Objects)
            Objects[key] = copy.ToArray();
    }

    public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        lock (Objects)
            return Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
    }

    public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        lock (Objects)
            return Task.FromResult(Objects.ContainsKey(key));
    }

    public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        if (FailNextDelete is { } failure)
        {
            FailNextDelete = null;
            return Task.FromException(failure);
        }

        lock (Objects)
            Objects.Remove(key);

        return Task.CompletedTask;
    }

    public string GetPublicUrl(string key) => throw new NotSupportedException("The private bucket has no public URL.");

    public string? TryGetPublicKey(string url) => null;

    public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);
}

/// <summary>
/// One org on the Agency plan (the verification on by default), a property in Italy and one in Switzerland outside a known canton (keeps no document), the flow with the closure and its
/// doubles, the committed policy pinned, the flags on, a segment in observation unless the test says otherwise. The database is in memory and can be opened by a second context (a twin
/// request).
/// </summary>
internal sealed class PresenceClosureWorld : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-presence-closure-" + Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<AppDbContext> _options;

    private readonly IdentityPolicyProvider _policy;
    private readonly DocumentFormatRegistry _formats = PresenceClosureKit.Formats();

    private PresenceClosureWorld(bool pinThePolicy, SaveChangesInterceptor[] interceptors)
    {
        _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"presence-closure-{Guid.NewGuid()}").AddInterceptors(interceptors).Options;
        Directory.CreateDirectory(_directory);
        _policy = PresenceClosureKit.Policy(_directory, pinThePolicy);
        Db = NewContext();
        Segments
            .Setup(segments => segments.ResolveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Segment);
        AccessCodes
            .Setup(codes => codes.ReleaseToGuestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("4321");
    }

    public Guid OrgId { get; } = Guid.NewGuid();

    public Guid PropertyId { get; } = Guid.NewGuid();

    public Guid SwissPropertyId { get; } = Guid.NewGuid();

    public AppDbContext Db { get; }

    public IDataProtectionProvider DataProtection { get; } = new EphemeralDataProtectionProvider();

    public FakeTimeProvider Clock { get; } = new(PresenceClosureKit.Now);

    public TestFeatureFlags Flags { get; } = new(FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted);

    public Mock<ICheckInLegalTexts> Registry { get; } = ActivePoseFlowTestVerifier.CreateRegistry();

    public IdentityVerificationOptions Limits { get; } = new();

    public ScriptedEvidenceCollector Collector { get; } = new();

    public SegmentResolution Segment { get; set; } = EvidenceBundles.Observation;

    public Mock<IIdentitySegmentService> Segments { get; } = new();

    public Mock<IAccessCodeService> AccessCodes { get; } = new();

    public Mock<IDomainEventRecorder> Events { get; } = new();

    public RecordingOperatorAlerts Alerts { get; } = new();

    public PresenceMemoryStorage Storage { get; } = new();

    public bool ModelsReady { get; set; } = true;

    /// <summary>The world; <paramref name="interceptors"/> sit on every context of its database (a save that is refused, the unique index of the log that the memory store has not).</summary>
    public static async Task<PresenceClosureWorld> CreateAsync(bool pinThePolicy = true, params SaveChangesInterceptor[] interceptors)
    {
        var world = new PresenceClosureWorld(pinThePolicy, interceptors);
        world.Db.Orgs.Add(new OrgEntity
        {
            Id = world.OrgId,
            Name = "Agenzia chiusura",
            Slug = $"chiusura-{world.OrgId:N}"[..20],
            DisplayName = "Agenzia chiusura",
            ContactEmail = "org@example.com",
            PlanTier = PlanTier.Agency,
        });
        world.Db.Properties.Add(Property(world.PropertyId, world.OrgId, "IT", "Roma", "00100"));
        world.Db.Properties.Add(Property(world.SwissPropertyId, world.OrgId, "CH", "Lugano", "6900"));
        await world.Db.SaveChangesAsync();
        return world;
    }

    public AppDbContext NewContext() => new(_options, null, DataProtection);

    /// <summary>The flow with the closure, on <paramref name="db"/> (the context of the world by default).</summary>
    public IdentityVerificationFlowService Flow(AppDbContext? db = null, Microsoft.Extensions.Logging.ILogger<IdentityVerificationFlowService>? logger = null)
    {
        var context = db ?? Db;
        return new IdentityVerificationFlowService(
            context,
            Mock.Of<IIdentityVerificationAgent>(),
            Alerts,
            AccessCodes.Object,
            Mock.Of<IEmailQueue>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            new StayGuestService(context, Mock.Of<IAlloggiatiCodeTableService>()),
            Storage,
            DataProtection,
            Clock,
            logger ?? NullLogger<IdentityVerificationFlowService>.Instance,
            domainEvents: Events.Object,
            legalTexts: Registry.Object,
            featureFlags: Flags,
            identityPolicy: _policy,
            biometricModels: new FakeBiometricModelStore(ModelsReady),
            identityOptions: Options.Create(Limits),
            presenceIssuer: new PresenceChallengeIssuer(),
            formats: _formats,
            frameReader: new FrameSequenceReader(Options.Create(Limits), Options.Create(new BiometricModelOptions()), NullLogger<FrameSequenceReader>.Instance),
            evidenceCollector: Collector,
            segments: Segments.Object,
            decisionEngine: new IdentityDecisionEngine());
    }

    /// <summary>
    /// A confirmed stay of an adult booker whose guest opened the identity step, gave the consent to the text served and has the CIE read and checked (coherent), its front kept
    /// encrypted; or, with <paramref name="keepsNoDocument"/>, at a property that keeps nothing (the two sides read, nothing kept). Loaded as the public endpoint loads it.
    /// </summary>
    public async Task<GuestCheckInSession> ReadyStayAsync(bool keepsNoDocument = false, AppDbContext? db = null)
    {
        var context = db ?? Db;
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            FirstName = "Anna",
            LastName = "Bianchi",
            Email = "anna@example.com",
            DateOfBirth = new DateTime(1990, 1, 1),
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            PropertyId = keepsNoDocument ? SwissPropertyId : PropertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 28),
            CheckOutDate = new DateTime(2026, 11, 30),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };
        var mrz = new DocumentCheck(true, true, false, true, true, true, true, false);
        var front = new StoredDocumentPicture(true, true, null, null, GeometryOk: true, ClassifiedSide: DocumentSides.Front, DHash: "0f1e2d3c4b5a6978");
        var back = new StoredDocumentPicture(null, true, mrz, null, GeometryOk: true, ClassifiedSide: DocumentSides.Back, DHash: "8796a5b4c3d2e1f0");
        var check = new StoredDocumentCheck(
            mrz,
            Sha256(PresenceClosureKit.FrontPicture),
            PresenceClosureKit.Now.UtcDateTime.AddMinutes(-1),
            BackSha256: Sha256(PresenceClosureKit.BackPicture),
            Front: front,
            Back: back,
            TwoSided: true,
            Coherence: keepsNoDocument ? null : PresenceClosureKit.PassedCoherence());
        var session = new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = OrgId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = PresenceClosureKit.Now.UtcDateTime.AddDays(7),
            Status = GuestCheckInSessionStatus.InCompilazione,
            Language = "it",
            CreatedAt = PresenceClosureKit.Now.UtcDateTime,
            UpdatedAt = PresenceClosureKit.Now.UtcDateTime,
            BiometricConsentAt = PresenceClosureKit.Now.UtcDateTime.AddMinutes(-2),
            BiometricConsentVersion = "TEST-biometric-v1",
            BiometricConsentTextHash = new string('a', 64),
            IdentityOpenedAt = PresenceClosureKit.Now.UtcDateTime.AddMinutes(-3),
            IdentityChoice = IdentityChoices.Automatic,
            IdentityChannel = IdentityChannels.AndroidChrome,
            IdentityFormatId = "it-cie-3.0",
            DocumentCheckJson = JsonSerializer.Serialize(check, Web),
        };
        context.Guests.Add(guest);
        context.Bookings.Add(booking);
        context.GuestCheckInSessions.Add(session);
        if (!keepsNoDocument)
        {
            var key = $"orgs/{OrgId:N}/guests/{guest.Id:N}/documents/{Guid.NewGuid():N}.bin";
            Storage.Objects[key] = DataProtection.CreateProtector(IdentityVerificationFlowService.DocumentImageContentPurpose).Protect(PresenceClosureKit.FrontPicture);
            context.DocumentImages.Add(new DocumentImage
            {
                OrgId = OrgId,
                GuestId = guest.Id,
                BookingId = booking.Id,
                FrontImageUrl = key,
                UploadedAt = PresenceClosureKit.Now.UtcDateTime.AddMinutes(-1),
                ProcessedAt = PresenceClosureKit.Now.UtcDateTime.AddMinutes(-1),
            });
        }

        await context.SaveChangesAsync();
        return await LoadAsync(session.Id, context);
    }

    /// <summary>The session as the public endpoint loads it (tracked, with its booking, property and guest), on <paramref name="db"/>.</summary>
    public async Task<GuestCheckInSession> LoadAsync(Guid sessionId, AppDbContext? db = null) =>
        await (db ?? Db).GuestCheckInSessions
            .Include(s => s.Booking).ThenInclude(b => b.Property)
            .Include(s => s.Booking).ThenInclude(b => b.Guest)
            .SingleAsync(s => s.Id == sessionId);

    /// <summary>A challenge issued by the flow for <paramref name="session"/>: its proof.</summary>
    public async Task<string> IssueChallengeAsync(GuestCheckInSession session)
    {
        var issued = await Flow().IssuePresenceChallengeAsync(session, CancellationToken.None);
        Assert.NotNull(issued);
        return issued.ChallengeToken;
    }

    /// <summary>
    /// A challenge stored for <paramref name="session"/> and its proof, made as the flow makes them, for a world whose policy cannot be used (the flow then issues none, but a challenge
    /// issued before the policy stopped matching its pin can still come back).
    /// </summary>
    public async Task<string> StoreChallengeAsync(GuestCheckInSession session)
    {
        var challenge = new StoredPresenceChallenge(
            Guid.NewGuid(), [PresenceActions.TurnLeft, PresenceActions.LookUp, PresenceActions.TurnRight], Clock.GetUtcNow(), Clock.GetUtcNow().AddSeconds(90), 1);
        session.DocumentCheckJson = JsonSerializer.Serialize(PresenceClosureKit.ReadCheck(session) with { Presence = challenge }, Web);
        await Db.SaveChangesAsync();
        string[] bound =
        [
            session.Id.ToString("N"),
            session.TokenHash,
            "1",
            session.BiometricConsentAt!.Value.Ticks.ToString(CultureInfo.InvariantCulture),
            challenge.Id.ToString("N"),
        ];
        return DataProtection.CreateProtector(IdentityVerificationFlowService.PresenceTokenPurpose, bound).Protect(challenge.Id.ToString("N"));
    }

    /// <summary>The log rows of the booking of <paramref name="session"/>, read by another context.</summary>
    public async Task<List<BiometricVerificationLog>> LogRowsAsync(GuestCheckInSession session)
    {
        await using var other = NewContext();
        return await other.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().Where(row => row.BookingId == session.BookingId).ToListAsync();
    }

    /// <summary>The session as it was saved, read by another context.</summary>
    public async Task<GuestCheckInSession> ReadBackAsync(Guid sessionId)
    {
        await using var other = NewContext();
        return await other.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static Property Property(Guid id, Guid orgId, string country, string city, string postalCode) => new()
    {
        Id = id,
        OrgId = orgId,
        OwnerId = "auth0|owner",
        Name = "Casa Test",
        Address = "Via Test 1",
        City = city,
        PostalCode = postalCode,
        Country = country,
        Bedrooms = 1,
        Bathrooms = 1,
        MaxGuests = 2,
        NightlyRate = 100m,
    };

    private static string Sha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
}

/// <summary>
/// The unique index of the log on (booking, key) (<see cref="BiometricVerificationLog.IdempotencyIndexName"/>) for a store that has none: a save that adds a row whose key a row of the
/// same booking already saved through any context of the options fails the way PostgreSQL fails it, a <see cref="DbUpdateException"/> that carries the 23505 of that index. It lets the
/// closure be tried here at the very branch that PostgreSQL feeds in <c>PresenceClosurePostgresTests</c>.
/// </summary>
internal sealed class UniqueIdempotencyKeyInterceptor : SaveChangesInterceptor
{
    private readonly object _gate = new();
    private readonly HashSet<(Guid BookingId, string Key)> _saved = [];
    private List<(Guid BookingId, string Key)> _pending = [];

    /// <summary>The saves refused so far.</summary>
    public int Refused { get; private set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var added = eventData.Context!.ChangeTracker.Entries<BiometricVerificationLog>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.IdempotencyKey is not null)
            .Select(entry => (entry.Entity.BookingId, entry.Entity.IdempotencyKey!))
            .ToList();
        lock (_gate)
        {
            if (added.Any(_saved.Contains))
            {
                Refused++;
                throw new DbUpdateException(
                    "save failed",
                    new PostgresException(
                        "duplicate key value violates unique constraint",
                        "ERROR",
                        "ERROR",
                        PostgresErrorCodes.UniqueViolation,
                        constraintName: BiometricVerificationLog.IdempotencyIndexName));
            }

            _pending = added;
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _saved.UnionWith(_pending);
            _pending = [];
        }

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }
}
