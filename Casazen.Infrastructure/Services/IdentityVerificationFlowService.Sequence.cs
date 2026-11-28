using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The closure of an attempt of the presence (E73/F01/US01/T02, NF-192, NF-193, NF-194, ADR-013-R17, R18, R19, decision D8): the part of <see cref="IdentityVerificationFlowService"/>
/// behind <c>POST api/public/checkin/{token}/presence</c>. The backend reads the frames of the same session, runs the pipeline of the presence, lets the versioned policy decide
/// (<see cref="IdentityDecisionEngine"/>) and writes the outcome atomically.
/// </summary>
/// <remarks>
/// <para><b>Idempotent</b> (AC-NF194-1): the explicit key of the attempt (<c>Idempotency-Key</c>) is answered first, before any lock and without reading a frame, also after the challenge
/// expired: the key of the last attempt of the session (<see cref="GuestCheckInSession.IdentityLastAttemptKey"/>, with its public answer in
/// <see cref="GuestCheckInSession.IdentityLastAttemptResult"/>, <c>result</c> or <c>result:code,code</c>) or a log row of the booking with that key. The same check is repeated under the
/// lock of the row of the session (the twin that waited for the lock finds the outcome of the first), and the unique index on (booking, key) is the last line: a twin written by
/// another process makes the save fail, and its outcome is answered.</para>
/// <para><b>One lock, one save</b>: the lock of the row of the session (<c>SELECT … FOR UPDATE</c>, the semaphores in memory) is the one of the pose and of the presence challenge. Under
/// it, the guards of today's selfie (in their order), the challenge (present, of this session and attempt, not expired), the pictures of the document, the frames, the pipeline within
/// <c>IdentityVerification:Presence:SequenceTimeoutMs</c>, the segment and the decision, then ONE save: the challenge consumed; a guided retry counted with the answer of its key;
/// or the log row of the decision (outcome, decision mode, shadow outcome, policy version and hash, format, channel, key, reasons, thresholds of the policy, model, duration), the
/// outcome and the closure of the session, the perceptual hashes of the document dropped, and on an approval the event <c>IdVerificationCompleted</c>.</para>
/// <para><b>After the commit</b>, the effects of today: an approval deletes the kept pictures, resolves the alerts and releases the lock code; a review goes through
/// <see cref="OnReviewCommittedAsync"/> (where the evidence of the reviewer is kept, before the pictures are deleted) and is handed to the operators (<see cref="FinishReviewAsync"/>).
/// They run with no token of the request (a client that drops does not make them skip), and what a failure of them leaves undone is finished, once, by the next request with the same
/// key (<see cref="FinishInterruptedEffectsAsync"/>): the steps that can be repeated, under the lock of the row of the session.</para>
/// <para><b>Fail closed</b>: a policy that is not usable, models that are not loaded, a model that fails, a time that is over: a person decides, never an approval; the request for a
/// person stays open.</para>
/// <para><b>Privacy</b>: the frames and the pictures of the document are in memory only and zeroed when the request ends (<see cref="FrameSequence.Dispose"/>, <c>ImageBytes.Zero</c>);
/// the log has the session, the codes, the counts and the type of an exception, never a picture, a value read, a score or a name.</para>
/// </remarks>
public sealed partial class IdentityVerificationFlowService
{
    /// <summary>The longest public answer kept for the replay of a key (<see cref="GuestCheckInSession.IdentityLastAttemptResult"/>, 80 characters).</summary>
    internal const int LastAttemptResultMaxLength = 80;

    /// <summary>The longest name and version of a model in the log row (<see cref="BiometricVerificationLog.ModelName"/>, <see cref="BiometricVerificationLog.ModelVersion"/>).</summary>
    private const int ModelNameMaxLength = 100;

    private const int ModelVersionMaxLength = 50;

    /// <summary>
    /// The services of the closure are all there. The application registers them (the parts <c>Presence</c>, <c>Session</c>, <c>Foundations</c> and <c>Decision</c>); a flow built by
    /// hand without them answers that the presence is not available, and that the application does register them all is proved by <c>PresenceClosureTests</c>.
    /// </summary>
    internal bool PresenceClosureWired =>
        frameReader is not null && evidenceCollector is not null && segments is not null && decisionEngine is not null && identityPolicy is not null;

    public async Task<PresenceStepResult> SubmitPresenceSequenceAsync(GuestCheckInSession session, PresenceSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(submission);

        var key = submission.IdempotencyKey;
        if (!IdentityHeaders.IsValidIdempotencyKey(key))
            return NotRun(PresenceStepStatus.IdempotencyKeyRequired);

        // Off, or built without the services of the closure: the step is not there (the route answers 404 before, with the flags off).
        if (!SelfHostedOn || !PresenceClosureWired)
            return NotRun(PresenceStepStatus.SessionUnavailable);

        // a. The replay first: the same key is the same attempt, answered as it was, also after the challenge expired, and no frame is read.
        if (await FindReplayAsync(session, key, ct) is { } replay)
            return await AnswerReplayAsync(session, replay, ct);

        // h. The frames and the pictures of the document of this request live until it ends (the effects after the commit included) and are zeroed then, whatever happens.
        using var buffers = new PresenceBuffers();
        PresenceClosure closure;
        try
        {
            closure = await WithLockedPoseSessionAsync(session, PresenceClosure.Stale, () => CloseUnderLockAsync(session, submission, key, buffers, ct), ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, BiometricVerificationLog.IdempotencyIndexName))
        {
            // g. A twin of this attempt in another process wrote the row of this key first (the unique index on booking and key): its outcome is the answer.
            logger.LogInformation("Presence of session {SessionId}: a twin request closed the attempt first; its outcome is answered", session.Id);
            db.ChangeTracker.Clear();
            await db.Entry(session).ReloadAsync(ct);
            if (await FindReplayAsync(session, key, ct) is { } twin)
                return await AnswerReplayAsync(session, twin, ct);

            throw;
        }

        return await AfterCommitAsync(session, closure, buffers);
    }

    /// <summary>
    /// The checks of the closure under the lock of the row of the session, and the one save of its outcome. Nothing is written before the decision. What is read of the request goes
    /// into <paramref name="buffers"/>, which the caller zeroes when the request ends.
    /// </summary>
    private async Task<PresenceClosure> CloseUnderLockAsync(
        GuestCheckInSession session,
        PresenceSubmission submission,
        string key,
        PresenceBuffers buffers,
        CancellationToken ct)
    {
        // b. The replay again, under the lock: the twin that waited for it finds the outcome of the first (whose effects are the first's to run).
        if (await FindReplayAsync(session, key, ct) is { } replay)
            return PresenceClosure.Replay(replay);

        // c. The guards. A closed verification takes no new attempt; the request for a person stays open.
        if (session.IdentityClosedAt is not null || session.BiometricOutcome == BiometricOutcome.Accept)
            return PresenceClosure.Refused(PresenceStepStatus.AlreadyClosed);

        if (session.IdentityOpenedAt is null || session.IdentityChoice == IdentityChoices.Assisted)
            return PresenceClosure.Refused(PresenceStepStatus.ChallengeRequired);

        // The hand-offs of today's selfie, in its order: a person has the verification, the organization does not offer it, a minor, the bound guest left, no text of the consent.
        var context = await ContextAsync(session, ct);
        if (InOperatorPath(session, context))
            return await HandOffUnderLockAsync(session, context, key, null, ct);

        if (!context.Enabled)
            return await HandOffUnderLockAsync(session, context, key, VerificationAlertReasons.BiometricsDisabled, ct);

        if (context.IsMinor)
            return await HandOffUnderLockAsync(session, context, key, VerificationAlertReasons.MinorExcluded, ct);

        if (context.BoundGuestMissing)
            return await HandOffUnderLockAsync(session, context, key, VerificationAlertReasons.Review, ct);

        if (!context.ConsentTextServed)
            return await HandOffUnderLockAsync(session, context, key, VerificationAlertReasons.LegalTextNotApproved, ct);

        var stored = ReadCheck(session);
        if (stored?.Check.HolderIsMinor == true)
            return await HandOffUnderLockAsync(session, context, key, VerificationAlertReasons.MinorExcluded, ct);

        if (!HasCurrentConsent(session, context))
            return PresenceClosure.Refused(PresenceStepStatus.ConsentRequired);

        if (stored is null || !DocumentComplete(session, stored))
            return PresenceClosure.Refused(PresenceStepStatus.DocumentRequired);

        // The challenge: present, its proof of this session and attempt, not expired.
        if (stored.Presence is not { } challenge)
            return PresenceClosure.Refused(PresenceStepStatus.ChallengeRequired);

        if (!TryValidatePresenceToken(session, submission.ChallengeToken, out var token))
        {
            return PresenceClosure.Refused(token == PresenceTokenStatus.Expired ? PresenceStepStatus.ChallengeExpired : PresenceStepStatus.ChallengeRequired);
        }

        // d. The front of the document: the picture sent again (a property that keeps no document), recognised by its SHA-256, or the kept one, decrypted.
        byte[] documentBytes;
        if (submission.DocumentPhoto is not null)
        {
            documentBytes = await ImageBytes.ReadAllAsync(submission.DocumentPhoto, MaxImageBytes, ct);
            buffers.Document = documentBytes;
            if (!string.Equals(Sha256(documentBytes), stored.PhotoSha256, StringComparison.Ordinal))
                return PresenceClosure.Refused(PresenceStepStatus.DocumentPhotoMismatch);
        }
        else if (context.DocumentNotArchived)
        {
            return PresenceClosure.Refused(PresenceStepStatus.DocumentPhotoRequired);
        }
        else
        {
            // The storage and Data Protection errors propagate (the guest tries again with the same key): they say nothing about the guest.
            var kept = await LoadStoredDocumentAsync(session, ct);
            buffers.Document = kept;
            if (kept is null)
                return PresenceClosure.Refused(PresenceStepStatus.DocumentRequired);

            documentBytes = kept;
        }

        // A property that keeps nothing: the back of a document of two sides comes again too, and must be the one that was read.
        if (context.DocumentNotArchived && stored.BackSha256 is not null)
        {
            if (submission.DocumentPhotoBack is null)
                return PresenceClosure.Refused(PresenceStepStatus.DocumentPhotoRequired);

            var back = await ImageBytes.ReadAllAsync(submission.DocumentPhotoBack, MaxImageBytes, ct);
            buffers.Back = back;
            if (!string.Equals(Sha256(back), stored.BackSha256, StringComparison.Ordinal))
                return PresenceClosure.Refused(PresenceStepStatus.DocumentPhotoMismatch);
        }

        // d. The frames, in memory, with the limits of IdentityVerification:Presence: a request that breaks one is refused and nothing is consumed.
        var parts = submission.Frames.Select(frame => new FramePart(frame.Content, frame.Length, frame.ContentType)).ToList();
        var read = await frameReader!.ReadAsync(parts, submission.TimestampsJson, clock.GetUtcNow(), ct);
        if (!read.Succeeded)
        {
            return PresenceClosure.Refused(string.Equals(read.ErrorCode, IdentityApiErrors.PresenceSequenceTooLarge, StringComparison.Ordinal)
                ? PresenceStepStatus.SequenceTooLarge
                : PresenceStepStatus.SequenceInvalid);
        }

        var sequence = read.Sequence;
        buffers.Sequence = sequence;
        var started = Stopwatch.GetTimestamp();
        var snapshot = identityPolicy!.Snapshot;
        var (evidence, fault) = await CollectEvidenceAsync(session, sequence, challenge, documentBytes, snapshot, ct);

        // The segment of the property, the format and the channel: what the policy may do. An error of the database propagates: it never approves anybody.
        SegmentResolution? segment = null;
        if (snapshot.IsReady)
            segment = await segments!.ResolveAsync(session.OrgId, session.Booking.PropertyId, session.IdentityFormatId, session.IdentityChannel, ct);

        var decision = decisionEngine!.Decide(BundleOf(session, stored, snapshot, evidence, fault, segment));
        var durationMs = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return await RecordDecisionAsync(session, stored, key, decision, evidence, snapshot, durationMs, ct);
    }

    /// <summary>
    /// The pipeline of the presence within <c>IdentityVerification:Presence:SequenceTimeoutMs</c>, or the fault that stopped it: models that are not loaded (or a section of
    /// <c>IdentityVerification</c> that is not valid) are <see cref="IdentityModelsFault.NotReady"/>, the time over <see cref="IdentityModelsFault.TimedOut"/>, an exception
    /// <see cref="IdentityModelsFault.Failed"/> (its type only is logged). Nothing runs without a usable policy: the engine reviews it as unavailable.
    /// </summary>
    private async Task<(PresenceEvidence? Evidence, IdentityModelsFault Fault)> CollectEvidenceAsync(
        GuestCheckInSession session,
        FrameSequence sequence,
        StoredPresenceChallenge challenge,
        byte[] documentFront,
        IdentityPolicySnapshot snapshot,
        CancellationToken ct)
    {
        if (!snapshot.IsReady)
            return (null, IdentityModelsFault.None);

        var options = identityOptions?.Value;
        if (biometricModels is not { IsAvailable: true } || options is null || options.Validate().Count > 0)
        {
            logger.LogWarning("Presence of session {SessionId}: the models or the section IdentityVerification are not available; a person decides", session.Id);
            return (null, IdentityModelsFault.NotReady);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.Presence.SequenceTimeoutMs);
        try
        {
            var evidence = await evidenceCollector!.CollectAsync(sequence, challenge, documentFront, snapshot.Policy, budget.Token);
            return (evidence, evidence.ModelsFault);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Presence of session {SessionId}: the evidence was not collected within {TimeoutMs} ms; a person decides", session.Id, options.Presence.SequenceTimeoutMs);
            return (null, IdentityModelsFault.TimedOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: a message could carry what was being read.
            logger.LogError("Presence of session {SessionId}: the evidence could not be collected ({ErrorType}); a person decides", session.Id, ex.GetType().Name);
            return (null, IdentityModelsFault.Failed);
        }
    }

    /// <summary>The evidence of the attempt for the engine: the policy, the document, the format and its assurance on the channel, the presence, the match, the faults, the segment and the guided retries.</summary>
    private IdentityEvidenceBundle BundleOf(
        GuestCheckInSession session,
        StoredDocumentCheck stored,
        IdentityPolicySnapshot snapshot,
        PresenceEvidence? evidence,
        IdentityModelsFault fault,
        SegmentResolution? segment)
    {
        var policy = snapshot.IsReady ? snapshot.Policy : null;
        var formatId = session.IdentityFormatId;
        var channel = session.IdentityChannel;
        return new IdentityEvidenceBundle(
            snapshot.State,
            snapshot.ReasonCode,
            DocumentComplete(session, stored),
            CoherenceOf(stored, formatId),
            formatId,
            channel,
            formats?.AssuranceFor(formatId, channel),
            evidence is not null && policy is not null ? evidence.Checks(policy) : [],
            evidence?.Match is { } match && policy is not null ? MatchSummary.From(match, policy.Match) : null,
            fault,
            segment,
            session.IdentityGuidedRetries,
            policy?.Closure.GuidedRetries ?? 0);
    }

    /// <summary>
    /// The coherence of the document from the entries the document step kept (<see cref="StoredDocumentCheck.Coherence"/>), by the one rule of <see cref="IdentityDocumentCoherences"/>:
    /// an identifying conflict, a failed check digit of the MRZ, an expired document and a value declared for the booking that differs from the document (IDENT-DECLARED) in this
    /// order; a pass only when every check the format requires holds (nothing declared for the booking is <c>not_applicable</c>, no conflict); unknown otherwise (no entries, as in
    /// Ticino, or a format the registry does not hold): never a pass.
    /// </summary>
    private IdentityDocumentCoherence CoherenceOf(StoredDocumentCheck stored, string? formatId)
    {
        if (stored.Coherence is not { Count: > 0 } entries)
            return IdentityDocumentCoherence.Unknown;

        return IdentityDocumentCoherences.Of(DocumentCoherenceReport.FromChecks(entries), formats?.Get(formatId));
    }

    /// <summary>e. The one save of the decision: a guided retry, or the closure of the verification with its log row.</summary>
    private async Task<PresenceClosure> RecordDecisionAsync(
        GuestCheckInSession session,
        StoredDocumentCheck stored,
        string key,
        IdentityDecision decision,
        PresenceEvidence? evidence,
        IdentityPolicySnapshot snapshot,
        int durationMs,
        CancellationToken ct)
    {
        var now = UtcNow();
        if (!decision.ClosesTheVerification)
        {
            // A guided retry: the challenge is consumed, the retry counted and the answer of the key kept; no log row, the verification goes on with a new challenge and a new key.
            var guidance = FitGuidance(IdentityVerificationGuestResults.Retry, decision.Guidance);
            session.DocumentCheckJson = JsonSerializer.Serialize(stored with { Presence = null }, Json);
            session.IdentityGuidedRetries += 1;
            RememberAnswer(session, key, IdentityVerificationGuestResults.Retry, guidance);
            session.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Presence of session {SessionId}: guided retry {Retry} ({Guidance}), mode {Mode}",
                session.Id, session.IdentityGuidedRetries, string.Join(',', guidance), decision.Mode);
            return PresenceClosure.Retry(guidance);
        }

        var policy = snapshot.IsReady ? snapshot.Policy : null;
        var attempt = session.BiometricAttempts + 1;
        var approved = decision.Final == IdentityFinalDecision.AutoApproved;
        var outcomeCode = decision.OutcomeCode ?? IdentityOutcomeCodes.Review;

        // The challenge is consumed and the perceptual hashes of the sides go: they served the check of a repeated side, which is over.
        session.DocumentCheckJson = JsonSerializer.Serialize(WithoutPerceptualHashes(stored) with { Presence = null }, Json);
        session.BiometricAttempts = attempt;
        session.BiometricOutcome = decision.LogOutcome;
        RememberAnswer(session, key, approved ? IdentityVerificationGuestResults.Ok : IdentityVerificationGuestResults.Operator, []);
        CloseIdentitySession(session, outcomeCode);
        session.UpdatedAt = now;

        var model = biometricModels?.FaceRecognition;
        var log = new BiometricVerificationLog
        {
            OrgId = session.OrgId,
            BookingId = session.BookingId,
            GuestId = session.Booking.GuestId,
            Outcome = decision.LogOutcome,
            Method = VerificationMethod.Biometric,
            Attempts = attempt,
            ModelName = Truncate(model?.Name, ModelNameMaxLength),
            ModelVersion = Truncate(model?.Version, ModelVersionMaxLength),
            ThresholdsJson = ThresholdsOf(policy),
            ReasonsJson = VerificationReasons.Serialize(decision.Reasons),
            Provisional = snapshot.Provisional,
            DurationMs = durationMs,
            PolicyVersion = policy?.Version,
            PolicyHash = snapshot.IsReady ? snapshot.Hash : null,
            DecisionMode = decision.Mode,
            ShadowOutcome = decision.ShadowOutcome,
            FormatId = session.IdentityFormatId,
            Channel = session.IdentityChannel,
            IdempotencyKey = key,
            CreatedAt = now,
        };
        db.BiometricVerificationLogs.Add(log);

        // An approval is announced on the bus in the same save as its row of the log, as the legacy path does: ids and the method only.
        if (approved)
        {
            domainEvents?.Record(
                DomainEventType.IdVerificationCompleted,
                session.BookingId,
                new IdVerificationCompletedPayload(session.BookingId, session.Booking.PropertyId, IdVerificationMethods.Biometric),
                orgId: session.OrgId);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Presence of session {SessionId} decided: outcome={Outcome} shadow={Shadow} mode={Mode} attempt={Attempt} durationMs={DurationMs}",
            session.Id, outcomeCode, decision.Shadow, decision.Mode, attempt, durationMs);
        return PresenceClosure.Decided(decision, evidence, log.Id);
    }

    /// <summary>
    /// f. The effects of the decision after its commit, as the legacy flow has them; then the answer, with the state of the verification after them. The decision is committed: the effects
    /// are not the request's any more, so they run with no token of it (<see cref="CancellationToken.None"/>). A guest whose connection drops, a page that is closed, must neither make the
    /// operators miss the hand-off nor leave the encrypted picture of the document in the bucket, and nobody is left to cancel them. What a failure of them leaves undone is finished by
    /// the next request with the same key (<see cref="FinishInterruptedEffectsAsync"/>).
    /// </summary>
    private async Task<PresenceStepResult> AfterCommitAsync(GuestCheckInSession session, PresenceClosure closure, PresenceBuffers buffers)
    {
        var ct = CancellationToken.None;
        switch (closure.Kind)
        {
            case PresenceClosureKind.Stale:
                return NotRun(PresenceStepStatus.SessionUnavailable);

            case PresenceClosureKind.Refused:
                return closure.Status == PresenceStepStatus.AlreadyClosed
                    ? NotRun(closure.Status, await GetStateAsync(session, ct))
                    : NotRun(closure.Status);

            case PresenceClosureKind.Replay:
                return await AnswerReplayAsync(session, closure.Answer!, ct);

            case PresenceClosureKind.Retry:
                return Answered(IdentityVerificationGuestResults.Retry, closure.Guidance, await GetStateAsync(session, ct));

            case PresenceClosureKind.HandedOff:
                return Answered(IdentityVerificationGuestResults.Operator, [], await GetStateAsync(session, ct));
        }

        var decision = closure.Decision!;
        if (decision.Final == IdentityFinalDecision.AutoApproved)
        {
            // The success path of today: the kept pictures go, the alerts are resolved, the lock code is released (screen and e-mail).
            await DeleteStoredDocumentAsync(session, ct);
            await db.SaveChangesAsync(ct);
            await alerts.ResolveAsync(session.OrgId, session.BookingId, ct);
            var code = await ReleaseCodeAsync(session, ct);
            var held = await HoldReasonAsync(session, code, ct);
            return Answered(IdentityVerificationGuestResults.Ok, [], await GetStateAsync(session, ct), code, held);
        }

        await OnReviewCommittedAsync(session, decision, buffers, closure.Evidence, closure.LogId, ct);
        await FinishReviewAsync(session);
        return Answered(IdentityVerificationGuestResults.Operator, [], await GetStateAsync(session, ct));
    }

    /// <summary>
    /// The effects of a review once it is committed, every step idempotent: the kept pictures of the document go (they can be deleted again) and, when the operators are not told yet
    /// (<see cref="GuestCheckInSession.OperatorRequestedAt"/>), they are told once (the alert and the "To do", saved with the session). It runs under the lock of the row of the session,
    /// read again under it, so the request that decided, a twin and a request that finds a review whose effects were interrupted do the same work one after the other and the operators
    /// are told once. A failure of the deletion does not skip the hand-off and is thrown after it: the page asks again and the replay finishes the deletion. Nothing is done for a session
    /// that is not a review of the closure.
    /// </summary>
    private async Task FinishReviewAsync(GuestCheckInSession session)
    {
        var ct = CancellationToken.None;
        var deletion = await WithLockedPoseSessionAsync<ExceptionDispatchInfo?>(session, null, async () =>
        {
            if (session.BiometricOutcome != BiometricOutcome.Escalate || session.IdentityClosedAt is null)
                return null;

            ExceptionDispatchInfo? failure = null;
            try
            {
                await DeleteStoredDocumentAsync(session, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The type only: a message could carry what was being read. The operators are told all the same; the failure is thrown once the lock is released and the hand-off is
                // committed (a throw inside would roll the hand-off back with the transaction of the row lock).
                logger.LogError("Presence of session {SessionId}: the kept pictures of the document could not be deleted ({ErrorType}); the request is asked again", session.Id, ex.GetType().Name);
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            if (session.OperatorRequestedAt is null)
                await ToOperatorAsync(session, AlertReasonOf(session.IdentityOutcomeCode), ct);
            else
                await db.SaveChangesAsync(ct);

            return failure;
        }, ct);

        deletion?.Throw();
    }

    /// <summary>
    /// What the request that decided may have left undone after its save (its connection dropped, the hand-off or the deletion failed), finished by a request that is answered again for
    /// the same key, with the steps that can be repeated: a review is handed to the operators once and its kept pictures go (<see cref="FinishReviewAsync"/>); an approval deletes its kept
    /// pictures. The e-mail of the lock code and the resolution of the alerts of an approval are the request's that decided, and are not repeated.
    /// </summary>
    private async Task FinishInterruptedEffectsAsync(GuestCheckInSession session, string result)
    {
        if (string.Equals(result, IdentityVerificationGuestResults.Operator, StringComparison.Ordinal))
        {
            await FinishReviewAsync(session);
        }
        else if (string.Equals(result, IdentityVerificationGuestResults.Ok, StringComparison.Ordinal))
        {
            await DeleteStoredDocumentAsync(session, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The point between the commit of a review and its hand-off to the operators, BEFORE the kept pictures of the document are deleted: the evidence of the reviewer
    /// (E73/F01/US02/T01, ADR-013-R20, <c>Features:CheckInIdentityReviewEvidence</c>) is kept from here, from what this request holds in memory (<paramref name="buffers"/>: the frames
    /// and the front of the document, alive until the request ends and zeroed then) and the frames the server chose (<see cref="PresenceEvidence.Selection"/>, null when the attempt
    /// stopped before: a fault, or no frame chosen). It writes the line of the review (the checks that did not pass, by their codes, and the counts of the frames, never a value),
    /// then hands the evidence to <see cref="IdentityReviewEvidenceStore"/>, only for a review (never an approval) and with the rule of the flow on the property (Ticino keeps
    /// nothing). The evidence is optional: whatever fails here is logged by its type and the hand-off goes on. A twin of the same attempt that takes the lock of the row first
    /// finishes the hand-off (and the deletion) by itself: the evidence is taken from the frames of this request, which only this request has, never from the kept pictures in the bucket.
    /// </summary>
    private async Task OnReviewCommittedAsync(
        GuestCheckInSession session,
        IdentityDecision decision,
        PresenceBuffers buffers,
        PresenceEvidence? evidence,
        Guid? logId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sequence = buffers.Sequence;
        var notPassed = decision.Reasons
            .Where(reason => !string.Equals(reason.Result, VerificationReasons.Results.Passed, StringComparison.Ordinal))
            .Select(reason => reason.Check)
            .Distinct(StringComparer.Ordinal);
        logger.LogInformation(
            "Presence of session {SessionId}: the review is handed to a person (outcome {Outcome}, shadow {Shadow}, checks [{Checks}], {Frames} frames, {Chosen} chosen)",
            session.Id, decision.OutcomeCode, decision.Shadow, string.Join(',', notPassed), sequence?.Frames.Count ?? 0, evidence?.Selection?.MatchIndices.Count ?? 0);

        if (reviewEvidence is null || decision.Final != IdentityFinalDecision.Review || !reviewEvidence.EvidenceOn)
            return;

        try
        {
            var keepsNoDocument = await DocumentNotArchivedAsync(session.Booking.Property, ct);
            await reviewEvidence.SaveForReviewAsync(
                new ReviewEvidenceInput(
                    session.OrgId,
                    session.BookingId,
                    session.Id,
                    session.IdentityGuestId,
                    logId,
                    keepsNoDocument,
                    buffers.Document,
                    sequence,
                    evidence?.Selection?.MatchIndices),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: the evidence is optional and the operators must be told all the same.
            logger.LogError("Presence of session {SessionId}: the evidence of the review was not kept ({ErrorType})", session.Id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// What the request read and must not outlive it: the frames (<see cref="FrameSequence.Dispose"/> zeroes every JPEG and every decoded picture) and the pictures of the document
    /// (zeroed). Owned by <see cref="SubmitPresenceSequenceAsync"/> for the whole request, the effects after the commit included, and disposed when it ends, whatever happens.
    /// </summary>
    private sealed class PresenceBuffers : IDisposable
    {
        public FrameSequence? Sequence { get; set; }

        public byte[]? Document { get; set; }

        public byte[]? Back { get; set; }

        public void Dispose()
        {
            Sequence?.Dispose();
            ImageBytes.Zero(Document);
            ImageBytes.Zero(Back);
        }
    }

    /// <summary>
    /// A hand-off found by the guards of the closure (a person already has the verification, it is not offered, a minor, the bound guest left, no text of the consent): the kept
    /// pictures go and the operators are told (<paramref name="alertReason"/>; null for the operator path of <see cref="CloseOperatorPathAsync"/>), in the save of the hand-off, with
    /// the answer of the key, so a replay answers <c>operator</c> again. No log row: nothing was decided by the policy.
    /// </summary>
    private async Task<PresenceClosure> HandOffUnderLockAsync(GuestCheckInSession session, FlowContext context, string key, string? alertReason, CancellationToken ct)
    {
        RememberAnswer(session, key, IdentityVerificationGuestResults.Operator, []);
        if (alertReason is null)
        {
            await CloseOperatorPathAsync(session, context, ct);
        }
        else
        {
            await DeleteStoredDocumentAsync(session, ct);
            await ToOperatorAsync(session, alertReason, ct);
        }

        return PresenceClosure.HandedOff;
    }

    /// <summary>The reason of the alert of a review: a fault of the models or a policy that is not usable is a technical error; any other review is a review.</summary>
    private static string AlertReasonOf(string? outcomeCode)
    {
        return outcomeCode is IdentityOutcomeCodes.TechnicalError or IdentityOutcomeCodes.Unavailable
            ? VerificationAlertReasons.TechnicalError
            : VerificationAlertReasons.Review;
    }

    /// <summary>
    /// The attempt of <paramref name="key"/> already answered: the last attempt of the session, or a log row of the booking with that key (the outcome of the row). Null when the key
    /// is new. A replay does not take the effects of a decision again (the e-mail of the code, the resolution of the alerts, the evidence of the reviewer are the request's that took it);
    /// it only finishes, once, what that request may have left undone with the steps that can be repeated (<see cref="FinishInterruptedEffectsAsync"/>), and the row and the outcome,
    /// written in the one save, already put the session in the queue of the operators.
    /// </summary>
    private async Task<PresenceReplay?> FindReplayAsync(GuestCheckInSession session, string key, CancellationToken ct)
    {
        if (string.Equals(session.IdentityLastAttemptKey, key, StringComparison.Ordinal) && TryReadAnswer(session.IdentityLastAttemptResult, out var result, out var guidance))
            return new PresenceReplay(result, guidance);

        var outcome = await db.BiometricVerificationLogs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(log => log.OrgId == session.OrgId && log.BookingId == session.BookingId && log.IdempotencyKey == key)
            .OrderBy(log => log.CreatedAt)
            .Select(log => (BiometricOutcome?)log.Outcome)
            .FirstOrDefaultAsync(ct);
        if (outcome is null)
            return null;

        return new PresenceReplay(outcome == BiometricOutcome.Accept ? IdentityVerificationGuestResults.Ok : IdentityVerificationGuestResults.Operator, []);
    }

    /// <summary>
    /// The answer of an attempt already answered: the same result and guidance, the state now and, for an approval, the lock code of the screen again (as a passed selfie sent again
    /// had it: no new comparison, no new e-mail). Nothing of the frames is read; what the request that decided left undone after its save is finished first (the operators told once, the
    /// kept pictures deleted).
    /// </summary>
    private async Task<PresenceStepResult> AnswerReplayAsync(GuestCheckInSession session, PresenceReplay replay, CancellationToken ct)
    {
        // The request that decided may have stopped after its save: what it left undone is finished now, with the steps that can be repeated.
        await FinishInterruptedEffectsAsync(session, replay.Result);

        string? code = null;
        string? held = null;
        if (string.Equals(replay.Result, IdentityVerificationGuestResults.Ok, StringComparison.Ordinal))
        {
            code = await accessCodes.ReleaseToGuestAsync(session.BookingId, AccessCodeReleaseChannels.CheckInScreen, ct);
            held = await HoldReasonAsync(session, code, ct);
        }

        logger.LogInformation("Presence of session {SessionId}: the attempt of this key was answered again ({Result})", session.Id, replay.Result);
        return Answered(replay.Result, replay.Guidance, await GetStateAsync(session, ct), code, held) with { Replayed = true };
    }

    private static PresenceStepResult NotRun(PresenceStepStatus status, IdentityVerificationState? state = null) =>
        new(status, null, [], false, state);

    /// <summary>An answer to the guest: <paramref name="guidedRetryAvailable"/> is true when the answer offers the guided retry (<c>retry</c>).</summary>
    private static PresenceStepResult Answered(string result, IReadOnlyList<string>? guidance, IdentityVerificationState state, string? lockCode = null, string? lockCodeHeld = null) =>
        new(
            PresenceStepStatus.Done,
            result,
            guidance ?? [],
            string.Equals(result, IdentityVerificationGuestResults.Retry, StringComparison.Ordinal),
            state,
            lockCode,
            lockCodeHeld);

    /// <summary>Keeps the key of the attempt and its public answer, so the same key gets it again.</summary>
    private static void RememberAnswer(GuestCheckInSession session, string key, string result, IReadOnlyList<string> guidance)
    {
        session.IdentityLastAttemptKey = key;
        session.IdentityLastAttemptResult = EncodeAnswer(result, guidance);
    }

    /// <summary>The public answer as it is kept: <c>result</c>, or <c>result:code,code</c> with the guidance codes.</summary>
    internal static string EncodeAnswer(string result, IReadOnlyList<string> guidance) =>
        guidance.Count == 0 ? result : $"{result}:{string.Join(',', guidance)}";

    /// <summary>
    /// The guidance codes that fit in the kept answer (<see cref="LastAttemptResultMaxLength"/> characters), in their order: the first ones, the most specific of the checks. The answer
    /// of the attempt is the kept one, so the request and its replay say the same.
    /// </summary>
    internal static IReadOnlyList<string> FitGuidance(string result, IReadOnlyList<string> guidance)
    {
        var kept = new List<string>(guidance.Count);
        foreach (var code in guidance)
        {
            if (EncodeAnswer(result, [.. kept, code]).Length > LastAttemptResultMaxLength)
                break;

            kept.Add(code);
        }

        return kept;
    }

    /// <summary>Reads a kept answer: false for anything that is not one (a result the page does not know).</summary>
    internal static bool TryReadAnswer(string? kept, out string result, out IReadOnlyList<string> guidance)
    {
        result = string.Empty;
        guidance = [];
        if (string.IsNullOrWhiteSpace(kept))
            return false;

        var separator = kept.IndexOf(':', StringComparison.Ordinal);
        var head = separator < 0 ? kept : kept[..separator];
        if (head is not (IdentityVerificationGuestResults.Ok or IdentityVerificationGuestResults.Retry or IdentityVerificationGuestResults.Operator))
            return false;

        result = head;
        guidance = separator < 0 ? [] : kept[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return true;
    }

    /// <summary>The thresholds of the policy the decision was taken with, for <see cref="BiometricVerificationLog.ThresholdsJson"/>: <c>{}</c> without a usable policy.</summary>
    private static string ThresholdsOf(IdentityDecisionPolicy? policy)
    {
        if (policy is null)
            return "{}";

        return JsonSerializer.Serialize(
            new
            {
                policy.Version,
                policy.Provisional,
                policy.CaptureRules,
                Match = new { policy.Match.Aggregation, policy.Match.Frames, policy.Match.Accept, policy.Match.ReviewFloor, policy.Match.MinEachFrame },
                Liveness = new { policy.Presence.Liveness.Rule, policy.Presence.Liveness.Frames, policy.Presence.Liveness.MinScore },
                policy.Closure.GuidedRetries,
            },
            Json);
    }

    /// <summary>The checks of the document without the perceptual hashes of its sides.</summary>
    private static StoredDocumentCheck WithoutPerceptualHashes(StoredDocumentCheck stored) => stored with
    {
        Front = stored.Front is null ? null : stored.Front with { DHash = null },
        Back = stored.Back is null ? null : stored.Back with { DHash = null },
    };

    private static string Truncate(string? value, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>An attempt already answered for its key: the result and the guidance.</summary>
    private sealed record PresenceReplay(string Result, IReadOnlyList<string> Guidance);

    private enum PresenceClosureKind
    {
        Stale,
        Refused,
        Replay,
        Retry,
        HandedOff,
        Decided,
    }

    /// <summary>What the work under the lock ended with: for a decision, also the id of its log row (the evidence of a review is linked to it).</summary>
    private sealed record PresenceClosure(
        PresenceClosureKind Kind,
        PresenceStepStatus Status = PresenceStepStatus.Done,
        PresenceReplay? Answer = null,
        IReadOnlyList<string>? Guidance = null,
        IdentityDecision? Decision = null,
        PresenceEvidence? Evidence = null,
        Guid? LogId = null)
    {
        public static PresenceClosure Stale { get; } = new(PresenceClosureKind.Stale, PresenceStepStatus.SessionUnavailable);

        public static PresenceClosure HandedOff { get; } = new(PresenceClosureKind.HandedOff);

        public static PresenceClosure Refused(PresenceStepStatus status) => new(PresenceClosureKind.Refused, status);

        public static PresenceClosure Replay(PresenceReplay replay) => new(PresenceClosureKind.Replay, Answer: replay);

        public static PresenceClosure Retry(IReadOnlyList<string> guidance) => new(PresenceClosureKind.Retry, Guidance: guidance);

        public static PresenceClosure Decided(IdentityDecision decision, PresenceEvidence? evidence, Guid logId) =>
            new(PresenceClosureKind.Decided, Decision: decision, Evidence: evidence, LogId: logId);
    }
}
