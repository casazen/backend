using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The presence of the self-hosted identity step (E72/F01/US01, NF-192, ADR-013-R17) and its closure (E73/F01/US01/T02, NF-194, ADR-013-R18, R19). This part of the controller holds the
/// endpoints of the presence; its services come as <c>[FromServices]</c> parameters, so the constructor of the class does not change, and they are registered in
/// <c>IdentityVerificationServiceCollectionExtensions.Presence.cs</c> and <c>.Decision.cs</c>.
/// </summary>
public partial class PublicGuestCheckInController
{
    /// <summary>
    /// The hard cap of a request of <c>/presence</c>: 12 MB and 64 KB of multipart headers, buffered in memory up to that size
    /// (<see cref="RequestFormLimitsAttribute.MemoryBufferThreshold"/>), so a frame or a picture of the document is never spilled to a temporary file (ADR-013-R2). The configured
    /// limits of the sequence (<c>IdentityVerification:Presence:MaxSequenceBytes</c> and the others) are checked by the reader of the frames, under this cap.
    /// </summary>
    public const int MaxPresenceRequestBytes = (12 * 1024 * 1024) + (64 * 1024);

    /// <summary>
    /// A presence challenge for the next attempt of the guest who verifies (E72/F01/US01/T01): <c>{ challengeToken, steps, stepMs, leadInMs, expiresAt, targetFps,
    /// maxFrames, maxFrameSide }</c>. The steps are several moves drawn at random from the policy (more than one bit), the proof is bound to the session, the attempt and
    /// the challenge, and a new call replaces the previous challenge, whose proof stops working. Never cached. 404 for an unknown, expired or completed link and, before
    /// anything is read, while <see cref="FeatureFlags.CheckInIdentityVerification"/> or <see cref="FeatureFlags.CheckInIdentitySelfHosted"/> is off; 409
    /// <c>identity_already_closed</c> once the verification is closed; 409 <c>presence_challenge_unavailable</c> with the <c>state</c> of the verification while the session is
    /// not ready for the presence (the identity step is not opened, the consent is not the current one, the document is not complete, a person already has the
    /// verification), or the policy, the models or the section <c>IdentityVerification</c> (a limit out of its range) are not available: the page then offers the
    /// assisted path, which is always open.
    /// </summary>
    [HttpPost("{token}/presence-challenge")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [EnableRateLimiting(RateLimitPolicies.GuestIdentity)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<PresenceChallengeDto>> IssuePresenceChallenge(string token, [FromServices] IIdentityPresenceFlow presenceFlow)
    {
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        // A closed verification takes no presence step any more; the request for a person stays open.
        if (session.IdentityClosedAt is not null)
            return this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.IdentityAlreadyClosed, "CheckInIdentityAlreadyClosed");

        var challenge = await presenceFlow.IssuePresenceChallengeAsync(session, HttpContext.RequestAborted);
        if (challenge is not null)
            return Ok(PresenceChallengeDto.From(challenge));

        var state = await verificationFlow.GetStateAsync(session, HttpContext.RequestAborted);
        var unavailable = this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.PresenceChallengeUnavailable, "CheckInPresenceChallengeUnavailable");
        if (unavailable.Value is ProblemDetails problem)
            problem.Extensions["state"] = PublicIdentityVerificationStateDto.From(state);

        return unavailable;
    }

    /// <summary>
    /// The presence of the guest and the closure of the attempt (E73/F01/US01/T02, NF-192, NF-194, ADR-013-R17..R19): multipart <c>frames</c> (JPEG parts, in their order),
    /// <c>timestamps</c> (a JSON array of integers in milliseconds, the first 0, strictly increasing), <c>challengeToken</c> (the proof of <c>/presence-challenge</c>) and, for a property
    /// that keeps no document (Ticino), <c>documentPhoto</c> and <c>documentPhotoBack</c> (the pictures that were read); the header <c>Idempotency-Key</c> (8 to 64 letters, digits or
    /// hyphens, one per attempt, the same when a request is repeated) is required. The server validates the sequence, chooses the frames, judges their liveness, compares them with the
    /// portrait of the document, and the versioned policy decides: <c>{ result: ok | retry | operator, guidance, guidedRetryAvailable, state, lockCode? }</c>
    /// (<see cref="PresenceStepResponseDto"/>). The same key gets the same answer, without a second attempt, also after the challenge expired. A fault of a model or a time over never
    /// approves: the answer is <c>operator</c>, and the request for a person stays open.
    /// <para>
    /// 404 for an unknown, expired or completed link and, before anything is read, with <see cref="FeatureFlags.CheckInIdentityVerification"/> or
    /// <see cref="FeatureFlags.CheckInIdentitySelfHosted"/> off; 400 <c>idempotency_key_required</c>; 409 <c>identity_already_closed</c> (with the state),
    /// <c>presence_challenge_required</c>, <c>presence_challenge_expired</c>, <c>biometric_consent_required</c>, <c>document_check_required</c>, <c>document_photo_mismatch</c>; 400
    /// <c>document_photo_required</c>, <c>document_photo_type_unsupported</c>; 413 <c>presence_sequence_too_large</c>; 422 <c>presence_sequence_invalid</c>. None of them consumes the
    /// challenge or writes anything. The body is held in memory with a hard cap (<see cref="MaxPresenceRequestBytes"/>) and the frames are zeroed when the request ends.
    /// </para>
    /// </summary>
    [HttpPost("{token}/presence")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [EnableRateLimiting(RateLimitPolicies.GuestIdentityUpload)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [RequestSizeLimit(MaxPresenceRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxPresenceRequestBytes, MultipartBodyLengthLimit = MaxPresenceRequestBytes)]
    public async Task<ActionResult<PresenceStepResponseDto>> SubmitPresence(
        string token,
        [FromServices] IIdentityPresenceFlow presenceFlow,
        [FromForm] List<IFormFile>? frames = null,
        [FromForm] string? timestamps = null,
        [FromForm] string? challengeToken = null,
        [FromForm] IFormFile? documentPhoto = null,
        [FromForm] IFormFile? documentPhotoBack = null,
        [FromHeader(Name = IdentityHeaders.IdempotencyKey)] string? idempotencyKey = null)
    {
        // The gates answer first; this keeps a direct call honest too.
        if (!SelfHostedIdentityOn())
            return NotFound();

        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        // The explicit key of the attempt: without it a request the network repeated could not be told from a new attempt, so nothing is run.
        if (!IdentityHeaders.IsValidIdempotencyKey(idempotencyKey))
            return this.ApiProblem(StatusCodes.Status400BadRequest, IdentityApiErrors.IdempotencyKeyRequired, "CheckInIdempotencyKeyRequired");

        if (!IsPictureOrAbsent(documentPhoto) || !IsPictureOrAbsent(documentPhotoBack))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_photo_type_unsupported", "CheckInDocumentPhotoTypeUnsupported");

        if (documentPhoto?.Length > CheckInDocumentLimits.MaxImageBytes || documentPhotoBack?.Length > CheckInDocumentLimits.MaxImageBytes)
            return this.ApiProblem(StatusCodes.Status413PayloadTooLarge, IdentityApiErrors.PresenceSequenceTooLarge, "CheckInPresenceSequenceTooLarge");

        var opened = new List<Stream>();
        PresenceStepResult result;
        try
        {
            // ADR-013-R17: in the request, not in a job. ADR-013-R2: the body is in memory (RequestFormLimits); the flow reads each part once and zeroes its copies.
            var parts = new List<PresenceFrameUpload>();
            foreach (var frame in frames ?? [])
            {
                var content = frame.OpenReadStream();
                opened.Add(content);
                parts.Add(new PresenceFrameUpload(content, frame.Length, frame.ContentType));
            }

            var front = Open(documentPhoto, opened);
            var back = Open(documentPhotoBack, opened);
            result = await presenceFlow.SubmitPresenceSequenceAsync(
                session,
                new PresenceSubmission(idempotencyKey, challengeToken, timestamps, parts, front, back),
                HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: a message could carry what was being read.
            logger.LogError("Error processing the presence of session {SessionId} ({ErrorType})", session.Id, ex.GetType().Name);
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "presence_processing_failed", "CheckInPresenceProcessingFailed");
        }
        finally
        {
            foreach (var stream in opened)
                await stream.DisposeAsync();
        }

        return result.Status switch
        {
            PresenceStepStatus.Done => Ok(PresenceStepResponseDto.From(result)),
            PresenceStepStatus.IdempotencyKeyRequired =>
                this.ApiProblem(StatusCodes.Status400BadRequest, IdentityApiErrors.IdempotencyKeyRequired, "CheckInIdempotencyKeyRequired"),
            PresenceStepStatus.AlreadyClosed when result.State is { } closed => IdentityClosedProblem(closed),
            PresenceStepStatus.AlreadyClosed =>
                this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.IdentityAlreadyClosed, "CheckInIdentityAlreadyClosed"),
            PresenceStepStatus.ChallengeRequired =>
                this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.PresenceChallengeRequired, "CheckInPresenceChallengeRequired"),
            PresenceStepStatus.ChallengeExpired =>
                this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.PresenceChallengeExpired, "CheckInPresenceChallengeExpired"),
            PresenceStepStatus.ConsentRequired =>
                this.ApiProblem(StatusCodes.Status409Conflict, BiometricConsentRequiredCode, "CheckInBiometricConsentRequired"),
            PresenceStepStatus.DocumentRequired =>
                this.ApiProblem(StatusCodes.Status409Conflict, "document_check_required", "CheckInDocumentCheckRequired"),
            PresenceStepStatus.DocumentPhotoRequired =>
                this.ApiProblem(StatusCodes.Status400BadRequest, "document_photo_required", "CheckInBiometricDocumentPhotoRequired"),
            PresenceStepStatus.DocumentPhotoMismatch =>
                this.ApiProblem(StatusCodes.Status409Conflict, "document_photo_mismatch", "CheckInDocumentPhotoMismatch"),
            PresenceStepStatus.SequenceTooLarge =>
                this.ApiProblem(StatusCodes.Status413PayloadTooLarge, IdentityApiErrors.PresenceSequenceTooLarge, "CheckInPresenceSequenceTooLarge"),
            PresenceStepStatus.SequenceInvalid =>
                this.ApiProblem(StatusCodes.Status422UnprocessableEntity, IdentityApiErrors.PresenceSequenceInvalid, "CheckInPresenceSequenceInvalid"),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// 409 <c>presence_sequence_required</c> (AC-NF192-4): with the self-hosted flow on the two-frame selfie of the legacy path is closed, its routes refuse, and the presence sequence
    /// (<c>/presence-challenge</c>, <c>/presence</c>) takes its place.
    /// </summary>
    private ObjectResult PresenceSequenceRequired() =>
        this.ApiProblem(StatusCodes.Status409Conflict, IdentityApiErrors.PresenceSequenceRequired, "CheckInPresenceSequenceRequired");

    /// <summary>A picture of the document that came again is a JPEG or a PNG with a content; absent is fine.</summary>
    private static bool IsPictureOrAbsent(IFormFile? file) => file is null || (file.Length > 0 && IsImage(file));

    private static Stream? Open(IFormFile? file, List<Stream> opened)
    {
        if (file is null)
            return null;

        var stream = file.OpenReadStream();
        opened.Add(stream);
        return stream;
    }
}
