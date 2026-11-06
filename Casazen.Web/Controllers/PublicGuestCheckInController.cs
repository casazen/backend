using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

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
    IIdentityVerificationFlowService verificationFlow,
    IFeatureFlags featureFlags,
    IStringLocalizer<SharedResources> localizer,
    ILogger<PublicGuestCheckInController> logger) : ControllerBase
{
    /// <summary>Problem code of a submit on a session the guest has already completed (409).</summary>
    public const string AlreadySubmittedCode = "checkin_already_submitted";

    /// <summary>Problem code of a language the page does not offer (400).</summary>
    public const string LanguageUnsupportedCode = "language_unsupported";

    /// <summary>Problem code of a selfie without a recorded biometric consent (409, ADR-013-R9).</summary>
    public const string BiometricConsentRequiredCode = "biometric_consent_required";

    /// <summary>
    /// Extension of the 400 of the submit (NF-40): a map from each field key (<c>Guests[1].DocumentNumber</c>) to the message key of
    /// <see cref="CheckInValidationKeys"/>, so the page, in five languages, translates the error itself.
    /// </summary>
    public const string ValidationCodesExtension = "codes";

    /// <summary>
    /// Largest request of <c>/document</c> and <c>/selfie</c> (10 MB). Their multipart body is buffered in memory up to this size
    /// (<see cref="RequestFormLimitsAttribute.MemoryBufferThreshold"/>), so an image is never spilled to a temporary file on disk
    /// (ADR-013-R2): the default threshold is 64 KB.
    /// </summary>
    public const int MaxImageRequestBytes = 10_000_000;

    private static readonly string[] AllowedImageTypes = ["image/jpeg", "image/jpg", "image/png"];

    /// <summary>
    /// Returns booking context for the guest form. Transitions session Inviato→InCompilazione on first open.
    /// Before completion the guests on file are returned with masked document numbers; after completion only the status is
    /// returned (no booking data, no PII), so the guest still sees that the check-in is done (A5-28). While
    /// <see cref="FeatureFlags.CheckInIdentityVerification"/> is on, an open session also says whether the property keeps no document
    /// (<c>documentNotArchived</c>, ADR-013-R15) and where the identity verification stands (E03/F04/US02/T04).
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
            // Two booleans, never the data (E06/F02/US01/T03): the link is anonymous.
            InvoiceRequested = view.InvoiceRequested,
            HasInvoiceData = view.HasInvoiceData,
            RequiredGuestFields = view.RequiredGuestFields ?? [],
            Language = GuestLanguages.Resolve(view.Language, Request.Headers.AcceptLanguage.ToString()),
            SupportedLanguages = GuestLanguages.Supported,
        };

        if (featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification)
            && await checkInService.GetSessionByTokenAsync(token) is { } session)
        {
            var state = await verificationFlow.GetStateAsync(session, HttpContext.RequestAborted);
            response.DocumentNotArchived = state.DocumentNotArchived;
            response.IdentityVerification = PublicIdentityVerificationStateDto.From(state);
        }

        return Ok(response);
    }

    /// <summary>
    /// Saves the language the guest chose on the page (NF-40: <c>it</c>, <c>en</c>, <c>de</c>, <c>fr</c>, <c>es</c>); the page and the emails
    /// of the session use it. 400 <c>language_unsupported</c> for another language, 404 for an unknown or expired link, 409
    /// <c>checkin_already_completed</c> once the guest submitted.
    /// </summary>
    [HttpPut("{token}/language")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<ActionResult<PublicCheckInLanguageResponse>> SetLanguage(
        string token,
        [FromBody] PublicCheckInLanguageRequest request)
    {
        var outcome = await checkInService.SetLanguageAsync(token, request.Language ?? string.Empty);
        return outcome switch
        {
            GuestCheckInLanguageOutcome.Saved =>
                Ok(new PublicCheckInLanguageResponse { Language = GuestLanguages.Normalize(request.Language)! }),
            GuestCheckInLanguageOutcome.Unsupported =>
                this.ApiProblem(StatusCodes.Status400BadRequest, LanguageUnsupportedCode, "CheckInLanguageUnsupported"),
            GuestCheckInLanguageOutcome.Completed =>
                this.ApiProblem(StatusCodes.Status409Conflict, "checkin_already_completed", "CheckInAlreadyCompleted"),
            _ => NotFound(),
        };
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
    /// (<c>Guests[1].DocumentNumber</c>, <c>MarketingEmail</c>) and the extension <c>codes</c> (the message key of each field, NF-40);
    /// duplicate submission → 409 <c>checkin_already_submitted</c>. The optional <c>invoice</c> block («Ti serve la fattura?», FIS-01) is saved as the
    /// invoice data of the booking in the same unit of work; one that is not valid → 422 <c>invoice_data_*</c> with the <c>field</c>, and nothing is
    /// saved, the check-in included.
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
            Invoice = request.Invoice?.ToInput(),
        };

        var result = await checkInService.SubmitAsync(token, submitRequest);

        if (result.Duplicate)
            return this.ApiProblem(StatusCodes.Status409Conflict, AlreadySubmittedCode, "CheckInAlreadySubmitted");

        if (result.ValidationErrors.Count > 0)
        {
            // The field keys stay as before; "codes" adds the message key of each, which the page translates in its own language.
            var codes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var error in result.ValidationErrors)
            {
                var key = error.ModelStateKey(nameof(request.Guests));
                ModelState.AddModelError(key, localizer[error.MessageKey, error.MessageArgs]);
                codes.TryAdd(key, error.MessageKey);
            }

            var invalid = ValidationProblem(ModelState);
            if (invalid is ObjectResult { Value: ValidationProblemDetails problem })
                problem.Extensions[ValidationCodesExtension] = codes;
            return invalid;
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
    /// The guest's answer to the biometric consent (E03/F04/US02/T04, ADR-013-R9): explicit, separate from the privacy notice and refusable.
    /// <c>{ consentGiven: true, consentVersion }</c> records the time and the version of the text; <c>{ consentGiven: false }</c> records the
    /// refusal and hands the verification to an operator, alerted at once. When the biometric verification is not offered (switched off for the
    /// organization, a minor) nothing is recorded and the answer is <c>operator</c>. 400 <c>biometric_consent_answer_required</c> without
    /// <c>consentGiven</c>, 400 <c>biometric_consent_version_required</c> for a consent without its version (at most 50 characters); 404 for an
    /// unknown, expired or completed link. Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default.
    /// </summary>
    [HttpPost("{token}/biometric-consent")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<ActionResult<BiometricConsentResponse>> RecordBiometricConsent(
        string token,
        [FromBody] BiometricConsentRequest request)
    {
        if (request.ConsentGiven is not { } consentGiven)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "biometric_consent_answer_required", "CheckInBiometricConsentAnswerRequired");

        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        var result = await verificationFlow.RecordConsentAsync(session, consentGiven, request.ConsentVersion, HttpContext.RequestAborted);
        if (result.Status == BiometricConsentStatus.VersionRequired)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "biometric_consent_version_required", "CheckInBiometricConsentVersionRequired");

        return Ok(new BiometricConsentResponse
        {
            ConsentRecorded = result.Status == BiometricConsentStatus.Recorded,
            Result = result.Status == BiometricConsentStatus.Recorded
                ? BiometricConsentResponse.RecordedResult
                : IdentityVerificationGuestResults.Operator,
            BiometricOffered = result.State.BiometricOffered,
            OperatorPathOpen = result.State.OperatorPathOpen,
            State = PublicIdentityVerificationStateDto.From(result.State),
        });
    }

    /// <summary>
    /// Uploads identity document image and extracts data via MRZ or OCR (NF-10, ADR-009-R22).
    /// Returns extracted fields for guest confirmation, with the capture guidance of the verification agent (E03/F04/US02/T04:
    /// <c>acceptable</c>, <c>guidanceCodes</c>). The checks of the document against the check-in data of the guest who verifies are kept on the
    /// session as flags. Outside Ticino, after the biometric consent, the photo is kept encrypted for the comparison with the selfie
    /// (<c>documentStored</c>) until the verification is over; in Ticino nothing is kept (ADR-013-R6, <c>documentNotArchived</c>). Supports
    /// passport (TD-3), ID card (TD-1), and residence permit. Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default.
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
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        if (document == null || document.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_required", "CheckInDocumentRequired");

        if (!IsImage(document))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_type_unsupported", "CheckInDocumentTypeUnsupported");

        try
        {
            // Read in memory only (ADR-013-R2): the multipart body is buffered in memory, the service zeroes its copy.
            await using var image = document.OpenReadStream();
            var result = await verificationFlow.SubmitDocumentAsync(session, image, HttpContext.RequestAborted);
            var reading = result.Reading;
            var fields = reading.Fields;

            return Ok(new DocumentUploadResponse
            {
                Success = reading.Method != DocumentReadingMethod.None,
                ExtractionMethod = reading.Method switch
                {
                    DocumentReadingMethod.Mrz => "MRZ",
                    DocumentReadingMethod.Ocr => "OCR",
                    _ => "None",
                },
                Confidence = reading.Confidence,
                CheckDigitsValid = reading.CheckDigitsValid,
                RequiresManualReview = reading.RequiresManualReview,
                Fields = fields is null
                    ? null
                    : new ExtractedDocumentFields
                    {
                        DocumentType = fields.DocumentType,
                        Surname = fields.Surname,
                        GivenNames = fields.GivenNames,
                        FullName = fields.FullName,
                        DocumentNumber = fields.DocumentNumber,
                        DateOfBirth = fields.DateOfBirth?.ToDateTime(TimeOnly.MinValue),
                        Nationality = fields.Nationality,
                        ExpirationDate = fields.ExpirationDate?.ToDateTime(TimeOnly.MinValue),
                        Sex = fields.Sex,
                        IssuingState = fields.IssuingState,
                    },
                Acceptable = result.Acceptable,
                GuidanceCodes = result.GuidanceCodes,
                DocumentStored = result.DocumentStored,
                DocumentNotArchived = result.DocumentNotArchived,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: a message could carry what was being read.
            logger.LogError("Error processing document upload for session {SessionId} ({ErrorType})", session.Id, ex.GetType().Name);
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "document_processing_failed", "CheckInDocumentProcessingFailed");
        }
    }

    /// <summary>
    /// The selfie of the guest who leaves the document, compared with the photo of the document by the verification agent in the request
    /// (ADR-013-R1, E03/F04/US02/T04): <c>{ result: ok | retry | operator, guidanceCodes, attemptsRemaining, operatorPathOpen, lockCode? }</c>.
    /// Only on <c>ok</c> the lock code is in the answer and e-mailed to the guest at once; a retry, a review, the last failed attempt, a refusal or
    /// an error release nothing, and a review, the last failed attempt or an error alert the operators. Nothing of the selfie is kept or logged
    /// (ADR-013-R2). 409 <c>biometric_consent_required</c> without the consent, 409 <c>document_check_required</c> before the document (or when
    /// its kept photo is gone), 400 <c>document_photo_required</c> for a property in Ticino without the photo of the document, 409
    /// <c>document_photo_mismatch</c> when that photo is not the one read. A minor, an organization that does not offer it and a session already
    /// with the operator answer <c>operator</c>. Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default.
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
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        if (selfie == null || selfie.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "selfie_required", "CheckInSelfieRequired");

        if (!IsImage(selfie))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "selfie_type_unsupported", "CheckInSelfieTypeUnsupported");

        if (documentPhoto is not null && (documentPhoto.Length == 0 || !IsImage(documentPhoto)))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_photo_type_unsupported", "CheckInDocumentPhotoTypeUnsupported");

        SelfieStepResult result;
        try
        {
            // ADR-013-R1: in the request, not in a job. ADR-013-R2: in memory only, zeroed by the agent and the pipeline.
            await using var selfieStream = selfie.OpenReadStream();
            await using var documentStream = documentPhoto?.OpenReadStream();
            result = await verificationFlow.SubmitSelfieAsync(session, selfieStream, documentStream, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Error processing selfie upload for session {SessionId} ({ErrorType})", session.Id, ex.GetType().Name);
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "selfie_processing_failed", "CheckInSelfieProcessingFailed");
        }

        return result.Status switch
        {
            SelfieStepStatus.ConsentRequired =>
                this.ApiProblem(StatusCodes.Status409Conflict, BiometricConsentRequiredCode, "CheckInBiometricConsentRequired"),
            SelfieStepStatus.DocumentRequired =>
                this.ApiProblem(StatusCodes.Status409Conflict, "document_check_required", "CheckInDocumentCheckRequired"),
            SelfieStepStatus.DocumentPhotoRequired =>
                this.ApiProblem(StatusCodes.Status400BadRequest, "document_photo_required", "CheckInBiometricDocumentPhotoRequired"),
            SelfieStepStatus.DocumentPhotoMismatch =>
                this.ApiProblem(StatusCodes.Status409Conflict, "document_photo_mismatch", "CheckInDocumentPhotoMismatch"),
            _ => Ok(new SelfieUploadResponse
            {
                Result = result.Result,
                GuidanceCodes = result.GuidanceCodes,
                AttemptsRemaining = result.AttemptsRemaining,
                OperatorPathOpen = result.OperatorPathOpen,
                LockCode = result.LockCode,
            }),
        };
    }

    /// <summary>
    /// The guest asks for a person (E03/F04/US02/T04, GDPR art. 22, AC-NF51-11): at any step, also after a retry or a failed attempt. The
    /// operators who decide the verifications of the property are alerted at once; a photo of the document kept for the comparison is deleted.
    /// 404 for an unknown, expired or completed link. Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default.
    /// </summary>
    [HttpPost("{token}/request-operator")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<ActionResult<OperatorRequestResponse>> RequestOperator(string token)
    {
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        var state = await verificationFlow.RequestOperatorAsync(session, HttpContext.RequestAborted);
        return Ok(new OperatorRequestResponse
        {
            Result = state.Result ?? IdentityVerificationGuestResults.Operator,
            OperatorPathOpen = state.OperatorPathOpen,
            State = PublicIdentityVerificationStateDto.From(state),
        });
    }

    private static bool IsImage(IFormFile file) =>
        AllowedImageTypes.Contains(file.ContentType.ToLowerInvariant(), StringComparer.Ordinal);
}
