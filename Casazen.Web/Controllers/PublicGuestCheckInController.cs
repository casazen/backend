using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Text.Json;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public (no-auth) guest check-in portal endpoints (AC3-AC5, AC8, US-020).
/// Route: api/public/checkin/{token}
/// </summary>
[ApiController]
[Route("api/public/checkin")]
[AllowAnonymous]
public class PublicGuestCheckInController(
    IGuestCheckInService checkInService,
    IAlloggiatiCodeTableService codeTableService,
    IAlloggiatiReportScheduler alloggiatiReportScheduler,
    IMrzExtractionService mrzService,
    IDocumentOcrService ocrService,
    IBiometricVerificationService biometricService,
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IStringLocalizer<SharedResources> localizer,
    ILogger<PublicGuestCheckInController> logger) : ControllerBase
{
    /// <summary>Problem code of a submit on a session the guest has already completed (409).</summary>
    public const string AlreadySubmittedCode = "checkin_already_submitted";

    /// <summary>
    /// Largest request of <c>/document</c> and <c>/selfie</c> (10 MB). Their multipart body is buffered in memory up to this size
    /// (<see cref="RequestFormLimitsAttribute.MemoryBufferThreshold"/>), so an image is never spilled to a temporary file on disk
    /// (ADR-013-R2): the default threshold is 64 KB.
    /// </summary>
    public const int MaxImageRequestBytes = 10_000_000;

    /// <summary>
    /// Returns booking context for the guest form. Transitions session Inviato→InCompilazione on first open.
    /// Before completion the guests on file are returned with masked document numbers; after completion only the status is
    /// returned (no booking data, no PII), so the guest still sees that the check-in is done (A5-28).
    /// </summary>
    [HttpGet("{token}")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<ActionResult<PublicCheckInContextResponse>> GetContext(string token)
    {
        var view = await checkInService.GetPublicViewAsync(token);
        if (view is null)
            return NotFound();

        if (view.IsCompleted)
            return Ok(new PublicCheckInContextResponse { Completed = true, Status = view.Status.ToString() });

        var response = new PublicCheckInContextResponse
        {
            Completed = false,
            Status = view.Status.ToString(),
            SessionId = view.SessionId,
            PropertyName = view.PropertyName,
            CheckInDate = view.CheckInDate,
            CheckOutDate = view.CheckOutDate,
            DeclaredGuests = view.DeclaredGuests,
            Guests = view.Guests?.Select(PublicCheckInGuestPrefill.From).ToList(),
            AvailableCodeTables = view.AvailableCodeTables,
            PrivacyNoticeVersion = view.PrivacyNoticeVersion,
            MarketingConsentVersion = view.MarketingConsentVersion,
        };

        return Ok(response);
    }

    /// <summary>
    /// Official Alloggiati codes (<c>list</c> = <c>comuni</c>, <c>stati</c>, <c>documenti</c> or <c>luoghi</c>) whose
    /// description matches <c>q</c>, for the guest form. Only with a usable, not yet completed session. Empty until an
    /// admin imports the tables (the form then asks for the names only).
    /// </summary>
    [HttpGet("{token}/codes")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<ActionResult<IEnumerable<AlloggiatiCodeEntryDto>>> SearchCodes(
        string token,
        [FromQuery] string? list,
        [FromQuery] string? q)
    {
        if (!AlloggiatiCodeLists.TryParse(list, out var tables))
            return this.ApiProblem(StatusCodes.Status400BadRequest, AlloggiatiController.CodeListUnknownCode, "AlloggiatiCodeListUnknown");

        if (await checkInService.GetSessionByTokenAsync(token) is null)
            return NotFound();

        var results = await codeTableService.SearchAsync(
            tables, q ?? string.Empty, AlloggiatiController.CodeSearchLimit, HttpContext.RequestAborted);
        return Ok(results.Select(AlloggiatiCodeEntryDto.From));
    }

    /// <summary>
    /// Accepts the data of every guest of the stay (CO-12) and the optional marketing consent, legacy or per channel (NF-05, the
    /// contract is on <see cref="PublicCheckInSubmitRequest"/>); the privacy notice shown is
    /// recorded with its version (CO-15), no consent is asked for the Alloggiati registration. On success schedules the Alloggiati job for
    /// the arrival day. Invalid data → 400 ValidationProblem with the errors keyed by request property
    /// (<c>Guests[1].DocumentNumber</c>, <c>MarketingEmail</c>);
    /// duplicate submission → 409 <c>checkin_already_submitted</c>.
    /// </summary>
    [HttpPost("{token}")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckInSubmit)]
    public async Task<IActionResult> Submit(string token, [FromBody] PublicCheckInSubmitRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var ip = ClientIp.GetString(HttpContext) ?? string.Empty;

        var submitRequest = new GuestCheckInSubmitRequest
        {
            Guests = request.Guests.Select(g => g.ToInput()).ToList(),
            MarketingConsent = request.MarketingConsent,
            MarketingConsentEmail = request.MarketingConsentEmail,
            MarketingConsentWhatsApp = request.MarketingConsentWhatsApp,
            MarketingEmail = request.MarketingEmail,
            ConsentIpAddress = ip,
        };

        var result = await checkInService.SubmitAsync(token, submitRequest);

        if (result.Duplicate)
            return this.ApiProblem(StatusCodes.Status409Conflict, AlreadySubmittedCode, "CheckInAlreadySubmitted");

        if (result.ValidationErrors.Count > 0)
        {
            foreach (var error in result.ValidationErrors)
            {
                ModelState.AddModelError(
                    error.ModelStateKey(nameof(request.Guests)),
                    localizer[error.MessageKey, error.MessageArgs]);
            }

            return ValidationProblem(ModelState);
        }

        if (!result.Success)
            return NotFound();

        // Alloggiati Web (art. 109 TULPS): scheduled for the arrival day in Europe/Rome, not now (the portal accepts
        // only today or yesterday as arrival date). Idempotent with the host check-in. The session stays Completo:
        // it becomes AlloggiatiInviato only with a real receipt.
        if (result.BookingId.HasValue)
        {
            try
            {
                await alloggiatiReportScheduler.EnsureScheduledAsync(result.BookingId.Value);
            }
            catch (Exception ex)
            {
                // The guest's data is saved: the host check-in schedules it again, and from the arrival day the booking
                // shows "to send manually" anyway.
                logger.LogError(ex, "Alloggiati report of booking {BookingId} could not be scheduled", result.BookingId);
            }
        }

        logger.LogInformation(
            "Public check-in submitted for booking {BookingId}, session {SessionId}",
            result.BookingId, result.SessionId);

        return Ok(new { sessionId = result.SessionId, message = "Check-in completed." });
    }

    /// <summary>
    /// Uploads identity document image and extracts data via MRZ or OCR (NF-10, ADR-009-R22).
    /// Returns extracted fields for guest confirmation. Does NOT store the image (Ticino compliance,
    /// ADR-013-R6). Supports passport (TD-3), ID card (TD-1), and residence permit.
    /// Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default (DEB-01): MRZ and OCR are placeholders.
    /// </summary>
    [HttpPost("{token}/document")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    [RequestSizeLimit(MaxImageRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxImageRequestBytes, MultipartBodyLengthLimit = MaxImageRequestBytes)]
    public async Task<ActionResult<DocumentUploadResponse>> UploadDocument(
        string token,
        [FromForm] IFormFile document)
    {
        // Validate session exists and is not completed
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        if (session.IsCompleted)
            return this.ApiProblem(
                StatusCodes.Status409Conflict,
                "checkin_already_completed",
                "CheckInAlreadyCompleted");

        // Validate file
        if (document == null || document.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_required", "CheckInDocumentRequired");

        // Validate content type (images only)
        var allowedTypes = new[] { "image/jpeg", "image/jpg", "image/png" };
        if (!allowedTypes.Contains(document.ContentType.ToLowerInvariant()))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_type_unsupported", "CheckInDocumentTypeUnsupported");

        try
        {
            // Process in-memory only (ADR-013-R6: Ticino zero retention, ADR-013-R2: never written to disk)
            using var imageStream = document.OpenReadStream();

            // Try MRZ extraction first (ADR-009-R22, ADR-013-R10)
            var mrzResult = await mrzService.ExtractAsync(imageStream, HttpContext.RequestAborted);

            if (mrzResult != null)
            {
                logger.LogInformation(
                    "MRZ extraction succeeded for session {SessionId}, confidence {Confidence}",
                    session.Id, mrzResult.Confidence);

                var response = new DocumentUploadResponse
                {
                    Success = true,
                    ExtractionMethod = "MRZ",
                    Confidence = mrzResult.Confidence,
                    CheckDigitsValid = mrzResult.CheckDigitsValid,
                    RequiresManualReview = !mrzResult.CheckDigitsValid || mrzResult.Confidence < 0.80,
                    Fields = new ExtractedDocumentFields
                    {
                        DocumentType = mrzResult.DocumentType,
                        Surname = mrzResult.Surname,
                        GivenNames = mrzResult.GivenNames,
                        DocumentNumber = mrzResult.DocumentNumber,
                        DateOfBirth = mrzResult.DateOfBirth,
                        Nationality = mrzResult.Nationality,
                        ExpirationDate = mrzResult.ExpirationDate,
                        Sex = mrzResult.Sex,
                        IssuingState = mrzResult.IssuingState,
                    }
                };

                return Ok(response);
            }

            // MRZ failed or not present, try OCR fallback (AC-NF10-2, ADR-009-R22)
            imageStream.Position = 0; // Reset stream for OCR
            var ocrResult = await ocrService.ExtractAsync(imageStream, HttpContext.RequestAborted);

            if (ocrResult != null)
            {
                logger.LogInformation(
                    "OCR extraction succeeded for session {SessionId}, confidence {Confidence}",
                    session.Id, ocrResult.Confidence);

                var response = new DocumentUploadResponse
                {
                    Success = true,
                    ExtractionMethod = "OCR",
                    Confidence = ocrResult.Confidence,
                    CheckDigitsValid = null, // OCR has no check digits
                    RequiresManualReview = ocrResult.Confidence < 0.80,
                    Fields = new ExtractedDocumentFields
                    {
                        FullName = ocrResult.FullName,
                        DocumentNumber = ocrResult.DocumentNumber,
                        DateOfBirth = ocrResult.DateOfBirth,
                        Nationality = ocrResult.Nationality,
                        ExpirationDate = ocrResult.ExpirationDate,
                        Sex = ocrResult.Sex,
                    }
                };

                return Ok(response);
            }

            // Both MRZ and OCR failed
            logger.LogWarning("Document extraction failed (MRZ and OCR) for session {SessionId}", session.Id);

            return Ok(new DocumentUploadResponse
            {
                Success = false,
                ExtractionMethod = "None",
                Confidence = 0.0,
                RequiresManualReview = true,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing document upload for session {SessionId}", session.Id);
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "document_processing_failed", "CheckInDocumentProcessingFailed");
        }
    }

    /// <summary>
    /// Uploads selfie for biometric verification (NF-11, ADR-013).
    /// Runs synchronously (NOT Hangfire) per ADR-013-R1 and returns outcome within 3s.
    /// All image processing is in-memory only with immediate deletion (ADR-013-R2).
    /// Max 3 attempts per session, then auto-escalates to operator queue (ADR-013-R5).
    /// Minors (DOB &lt; 18 years) are excluded from biometric verification (ADR-013-R9).
    /// Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default (DEB-01): liveness and face match are placeholders.
    /// </summary>
    [HttpPost("{token}/selfie")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    [RequestSizeLimit(MaxImageRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxImageRequestBytes, MultipartBodyLengthLimit = MaxImageRequestBytes)]
    public async Task<ActionResult<SelfieUploadResponse>> UploadSelfie(
        string token,
        [FromForm] IFormFile selfie,
        [FromForm] IFormFile? documentPhoto = null)
    {
        // Validate session exists and is not completed
        var session = await checkInService.GetSessionByTokenAsync(token);

        if (session is null)
            return NotFound();

        if (session.IsCompleted)
            return this.ApiProblem(
                StatusCodes.Status409Conflict,
                "checkin_already_completed",
                "CheckInAlreadyCompleted");

        // Load booking with guest for minor check
        var booking = await dbContext.Bookings
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == session.BookingId);

        if (booking is null)
            return NotFound();

        // Validate files
        if (selfie == null || selfie.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "selfie_required", "CheckInSelfieRequired");

        // Validate content types (images only)
        var allowedTypes = new[] { "image/jpeg", "image/jpg", "image/png" };
        if (!allowedTypes.Contains(selfie.ContentType.ToLowerInvariant()))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "selfie_type_unsupported", "CheckInSelfieTypeUnsupported");

        // ADR-013-R9: Minors (DOB < 18 years) excluded from biometric verification
        var guestDateOfBirth = booking.Guest?.DateOfBirth;
        if (guestDateOfBirth.HasValue)
        {
            var today = timeProvider.TodayInRomeAsDateOnly();
            var dob = DateOnly.FromDateTime(guestDateOfBirth.Value);
            var age = today.Year - dob.Year;
            if (dob > today.AddYears(-age))
                age--;

            if (age < 18)
            {
                logger.LogWarning(
                    "Biometric verification rejected for minor guest (age {Age}) in session {SessionId}",
                    age, session.Id);

                return this.ApiProblem(
                    StatusCodes.Status400BadRequest,
                    "biometric_minor_excluded",
                    "CheckInBiometricMinorExcluded");
            }
        }

        // ADR-013-R5: Check max attempts (3)
        const int maxAttempts = 3;
        if (session.BiometricAttempts >= maxAttempts)
        {
            return Ok(new SelfieUploadResponse
            {
                Outcome = BiometricOutcome.Escalate.ToString(),
                Attempts = session.BiometricAttempts,
                MaxAttempts = maxAttempts,
                AutoEscalated = true,
                MessageKey = "CheckInBiometricMaxAttemptsReached",
            });
        }

        try
        {
            // ADR-013-R1: Process synchronously (NOT Hangfire), return outcome within 3s
            // ADR-013-R2: In-memory only processing, deleted in finally blocks inside the service

            BiometricVerificationResult? verificationResult = null;

            // For now, we need a document photo to compare against
            // In a real scenario, this would come from a previously uploaded document
            if (documentPhoto == null || documentPhoto.Length == 0)
            {
                // Placeholder: In production, we'd retrieve the document photo from previous upload
                // For now, return escalate if no document photo is provided
                logger.LogWarning(
                    "No document photo provided for biometric verification in session {SessionId}",
                    session.Id);

                session.BiometricAttempts++;
                session.BiometricOutcome = BiometricOutcome.Escalate;
                session.UpdatedAt = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();

                return Ok(new SelfieUploadResponse
                {
                    Outcome = BiometricOutcome.Escalate.ToString(),
                    Attempts = session.BiometricAttempts,
                    MaxAttempts = maxAttempts,
                    AutoEscalated = false,
                    MessageKey = "CheckInBiometricDocumentPhotoRequired",
                });
            }

            // Validate document photo content type
            if (!allowedTypes.Contains(documentPhoto.ContentType.ToLowerInvariant()))
                return this.ApiProblem(StatusCodes.Status400BadRequest, "document_photo_type_unsupported", "CheckInDocumentPhotoTypeUnsupported");

            // Process biometric verification
            using var selfieStream = selfie.OpenReadStream();
            using var documentPhotoStream = documentPhoto.OpenReadStream();

            // Configurable thresholds (placeholder values, should come from configuration or BiometricSettings)
            const double acceptThreshold = 0.85;
            const double escalateThreshold = 0.70;

            verificationResult = await biometricService.VerifyAsync(
                selfieStream,
                documentPhotoStream,
                acceptThreshold,
                escalateThreshold,
                HttpContext.RequestAborted);

            // Update session
            session.BiometricAttempts++;
            session.BiometricOutcome = verificationResult.Outcome;
            session.UpdatedAt = DateTime.UtcNow;

            // ADR-013-R5: Auto-escalate after max attempts
            var autoEscalated = session.BiometricAttempts >= maxAttempts &&
                               verificationResult.Outcome != BiometricOutcome.Accept;

            if (autoEscalated)
            {
                session.BiometricOutcome = BiometricOutcome.Escalate;
            }

            // ADR-013-R3: Create BiometricVerificationLog (NO images, embeddings, or PII per ADR-013-R2)
            var log = new BiometricVerificationLog
            {
                OrgId = session.OrgId,
                BookingId = session.BookingId,
                GuestId = booking.GuestId,
                Outcome = session.BiometricOutcome ?? BiometricOutcome.Reject,
                Attempts = session.BiometricAttempts,
                ModelName = verificationResult.ModelName,
                ModelVersion = verificationResult.ModelVersion,
                ThresholdsJson = JsonSerializer.Serialize(new
                {
                    AcceptThreshold = acceptThreshold,
                    EscalateThreshold = escalateThreshold,
                    Similarity = verificationResult.Similarity,
                    IsLive = verificationResult.IsLive,
                }),
                CreatedAt = DateTime.UtcNow,
            };

            dbContext.BiometricVerificationLogs.Add(log);
            await dbContext.SaveChangesAsync();

            logger.LogInformation(
                "Biometric verification completed for session {SessionId}: outcome={Outcome}, attempts={Attempts}/{Max}, similarity={Similarity}",
                session.Id, verificationResult.Outcome, session.BiometricAttempts, maxAttempts, verificationResult.Similarity);

            var messageKey = verificationResult.Outcome switch
            {
                BiometricOutcome.Accept => "CheckInBiometricAccepted",
                BiometricOutcome.Escalate => autoEscalated ? "CheckInBiometricMaxAttemptsReached" : "CheckInBiometricEscalated",
                BiometricOutcome.Reject => session.BiometricAttempts >= maxAttempts ? "CheckInBiometricMaxAttemptsReached" : "CheckInBiometricRejected",
                _ => "CheckInBiometricEscalated",
            };

            return Ok(new SelfieUploadResponse
            {
                Outcome = session.BiometricOutcome?.ToString() ?? BiometricOutcome.Escalate.ToString(),
                Attempts = session.BiometricAttempts,
                MaxAttempts = maxAttempts,
                AutoEscalated = autoEscalated,
                MessageKey = messageKey,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing selfie upload for session {SessionId}", session.Id);
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "selfie_processing_failed", "CheckInSelfieProcessingFailed");
        }
    }
}
