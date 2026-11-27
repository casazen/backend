using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.GuestPortal;
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
public partial class PublicGuestCheckInController(
    IGuestCheckInService checkInService,
    IAlloggiatiCodeTableService codeTableService,
    ICheckInDocumentReader documentReader,
    IAlloggiatiReportScheduler alloggiatiReportScheduler,
    IIdentityVerificationFlowService verificationFlow,
    IFeatureFlags featureFlags,
    IStringLocalizer<SharedResources> localizer,
    ILogger<PublicGuestCheckInController> logger,
    ITouristTaxCollectionService? touristTaxCollection = null,
    ICheckInLegalTexts? legalTexts = null) : ControllerBase
{
    /// <summary>Problem code of a submit on a session the guest has already completed (409).</summary>
    public const string AlreadySubmittedCode = "checkin_already_submitted";

    /// <summary>Problem code of a language the page does not offer (400).</summary>
    public const string LanguageUnsupportedCode = "language_unsupported";

    /// <summary>Problem code of a selfie without a recorded biometric consent (409, ADR-013-R9).</summary>
    public const string BiometricConsentRequiredCode = "biometric_consent_required";

    /// <summary>Problem code of a consent that refers to a version the registry of the legal texts does not serve now (422, E22/F01/US02/T02).</summary>
    public const string BiometricConsentVersionUnknownCode = "biometric_consent_version_unknown";

    /// <summary>
    /// Extension of the 400 of the submit (NF-40): a map from each field key (<c>Guests[1].DocumentNumber</c>) to the message key of
    /// <see cref="CheckInValidationKeys"/>, so the page, in five languages, translates the error itself.
    /// </summary>
    public const string ValidationCodesExtension = "codes";

    /// <summary>
    /// Largest request of <c>/document</c> (10 MB). Its multipart body is buffered in memory up to this size
    /// (<see cref="RequestFormLimitsAttribute.MemoryBufferThreshold"/>), so an image is never spilled to a temporary file on disk
    /// (ADR-013-R2): the default threshold is 64 KB. <c>/selfie</c> has <see cref="MaxSelfieRequestBytes"/>.
    /// </summary>
    public const int MaxImageRequestBytes = 10_000_000;

    /// <summary>Two live captures and up to two document sides, 5 MiB each plus multipart headers, buffered in memory.</summary>
    public const int MaxSelfieRequestBytes = 4 * CheckInDocumentLimits.MaxImageBytes + 64 * 1024;

    private static readonly string[] AllowedImageTypes = ["image/jpeg", "image/jpg", "image/png"];

    /// <summary>
    /// Returns booking context for the guest form. Transitions session Inviato→InCompilazione on first open.
    /// Before completion the guests on file are returned with masked document numbers; after completion only the status and the
    /// slug of the org (public data, <c>orgSlug</c>) are returned (no booking data, no PII), so the guest still sees that the check-in is done (A5-28). While
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

        // The slug of the org is public data, so a completed link carries it too: the page on an agency's own domain needs it to tell a link of another agency (NF-140).
        if (view.IsCompleted)
            return Ok(new PublicCheckInContextResponse { Completed = true, Status = view.Status.ToString(), OrgSlug = view.OrgSlug });

        var response = new PublicCheckInContextResponse
        {
            Completed = false,
            Status = view.Status.ToString(),
            OrgSlug = view.OrgSlug,
            SessionId = view.SessionId,
            PropertyName = view.PropertyName,
            CheckInDate = view.CheckInDate,
            CheckOutDate = view.CheckOutDate,
            DeclaredGuests = view.DeclaredGuests,
            Guests = view.Guests?.Select(PublicCheckInGuestPrefill.From).ToList(),
            AvailableCodeTables = view.AvailableCodeTables,
            PrivacyNoticeVersion = view.PrivacyNoticeVersion,
            PrivacyNoticeOrgSlug = view.PrivacyNoticeOrgSlug,
            MarketingConsentVersion = view.MarketingConsentVersion,
            // Two booleans, never the data (E06/F02/US01/T03): the link is anonymous.
            InvoiceRequested = view.InvoiceRequested,
            HasInvoiceData = view.HasInvoiceData,
            RequiredGuestFields = view.RequiredGuestFields ?? [],
            AskVehiclePlate = view.AskVehiclePlate,
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

        if (PortalCheckInSessions.IsProof(token)
            && await checkInService.GetSessionByTokenAsync(token) is { } portalSession)
        {
            var rules = portalSession.Booking.Property.HouseRules;
            response.HouseRulesVersion = GuestHouseGuide.RulesVersion(rules);
            response.HouseRules = response.HouseRulesVersion is null ? null : rules;
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
    /// Reads one identity-document photo and returns the fields the guest can confirm. The photo is not stored, and it is read in the process whatever the flags
    /// (E70/F02/US02/T01). A missing provider or an unreadable picture answers 200 with <c>read: false</c> so the guest can type the data.
    /// <para>
    /// With the self-hosted flow on (<see cref="FeatureFlags.CheckInIdentityVerification"/> and <see cref="FeatureFlags.CheckInIdentitySelfHosted"/>, E71/F01/US03/T01) the answer
    /// also carries <c>fields</c> (<c>name</c>, <c>value</c>, <c>side</c>, <c>zone</c>, <c>confidence</c>, <c>source</c> of every value the zones of the card gave), <c>missing</c> (the
    /// fields of the check-in the guest has still to type) and <c>source</c> <c>local</c>. The values go to the guest who sent the photo, and only when the photo is of a guest of the
    /// stay (a photo of anybody else answers <c>guest_mismatch</c> with none); they are never stored, logged or put in a job. With the flow off the answer is the one of before.
    /// </para>
    /// </summary>
    [HttpPost("{token}/read-document")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    [RequestSizeLimit(CheckInDocumentLimits.MaxImageBytes + 64 * 1024)]
    [RequestFormLimits(MemoryBufferThreshold = CheckInDocumentLimits.MaxImageBytes + 64 * 1024)]
    public async Task<ActionResult<CheckInDocumentReadResponse>> ReadDocument(string token, IFormFile? file, [FromServices] IStayGuestService stayGuests)
    {
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        if (file is null || file.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "checkin_document_missing", "CheckInDocumentMissing");

        await using var stream = file.OpenReadStream();
        var zonedPrefill = SelfHostedIdentityOn();
        CheckInZonedRead? zoned = null;
        CheckInDocumentRead read;
        if (zonedPrefill)
        {
            zoned = await documentReader.ReadZonedAsync(stream, HttpContext.RequestAborted);
            read = zoned.Read;
        }
        else
        {
            read = await documentReader.ReadAsync(stream, HttpContext.RequestAborted);
        }

        if (read.Reason is "unsupported_file" or "too_large")
            return this.ApiProblem(StatusCodes.Status400BadRequest, $"checkin_document_{read.Reason}", "CheckInDocumentRejected");

        var response = CheckInDocumentReadResponse.From(read);
        if (read.Fields is { } fields)
        {
            var roster = await stayGuests.GetForBookingAsync(session.Booking, HttpContext.RequestAborted);
            var match = CheckInGuestMatch.Find(roster.Take(Math.Max(1, session.Booking.NumberOfGuests)).ToList(), fields);
            if (match is null)
            {
                var mismatch = CheckInDocumentReadResponse.From(CheckInDocumentRead.Failed("guest_mismatch"));
                return Ok(zonedPrefill ? mismatch.WithZoned([], CheckInPrefillFields.Asked) : mismatch);
            }

            response.GuestPosition = match.Value.Position;
            response.ConfirmGuest = match.Value.Confirm;
        }

        // Nothing read, nothing of a guest of the stay: no value of the zones leaves, and everything asked is still to be typed.
        if (zoned is not null)
            response.WithZoned(read.Read && read.Fields is not null ? zoned.Fields : [], read.Read && read.Fields is not null ? zoned.Missing : CheckInPrefillFields.Asked);

        return Ok(response);
    }

    [HttpPost("{token}/invoice")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckInSubmit)]
    public async Task<IActionResult> RequestInvoice(string token, PublicCheckInInvoiceDto request, CancellationToken ct)
    {
        return await checkInService.RequestInvoiceAsync(token, request.ToInput(), ct) ? NoContent() : NotFound();
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

        if (featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification)
            && await checkInService.GetSessionByTokenAsync(token) is { } session)
        {
            var state = await verificationFlow.GetStateAsync(session, HttpContext.RequestAborted);
            if (state.Step != IdentityVerificationSteps.Done && !state.OperatorRequested)
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "identity_verification_incomplete", "CheckInIdentityIncomplete");
        }

        var ip = ClientIp.GetString(HttpContext) ?? string.Empty;

        var submitRequest = new GuestCheckInSubmitRequest
        {
            Guests = request.Guests.Select(g => g.ToInput()).ToList(),
            MarketingConsent = request.MarketingConsent,
            MarketingConsentEmail = request.MarketingConsentEmail,
            MarketingConsentWhatsApp = request.MarketingConsentWhatsApp,
            MarketingEmail = request.MarketingEmail,
            ConsentIpAddress = ip,
            PrivacyNoticeAcknowledged = request.PrivacyNoticeAcknowledged,
            HouseRulesVersion = request.HouseRulesVersion,
            Invoice = request.Invoice?.ToInput(),
            VehiclePlate = request.VehiclePlate,
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
    /// The legal texts of the identity step for the stay of the link (E22/F01/US02/T02, NF-24, NF-50): the active version of the biometric consent and, for a property that
    /// keeps no document (Ticino, or Switzerland with a canton CasaZen does not know), of the Ticino notice, from the registry of the legal texts. <c>lang</c> is one of
    /// <c>it</c>, <c>en</c>, <c>de</c>, <c>fr</c>, <c>es</c> (the language of the session, then the browser, otherwise); each text says the <c>language</c> of the file served (the closest the
    /// registry has), its <c>version</c> (the id the consent posts back), <c>isDraft</c> (a draft is served outside production only: the page shows a «Bozza» badge) and its
    /// <c>contentHtml</c>, sanitized (headings, paragraphs and lists). In production only approved versions are served, so the list can lack the consent; the identity step is then
    /// not offered (<c>biometricOffered: false</c>) and an operator verifies. 404 for an unknown, expired or completed link. Behind
    /// <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default.
    /// </summary>
    [HttpGet("{token}/legal-texts")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<ActionResult<CheckInLegalTextsResponse>> GetLegalTexts(string token, [FromQuery] string? lang = null)
    {
        if (legalTexts is null)
            return NotFound();

        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        var state = await verificationFlow.GetStateAsync(session, HttpContext.RequestAborted);
        var language = GuestLanguages.Normalize(lang) ?? GuestLanguages.Resolve(session.Language, Request.Headers.AcceptLanguage.ToString());
        return Ok(CheckInLegalTextsResponse.From(language, legalTexts.GetForStay(state.DocumentNotArchived, language)));
    }

    /// <summary>
    /// The guest's answer to the biometric consent (E03/F04/US02/T04, ADR-013-R9): explicit, separate from the privacy notice and refusable.
    /// <c>{ consentGiven: true, consentVersion }</c> records the time and the version of the text, with the hash of that text (the version is the one that
    /// <c>GET .../legal-texts</c> serves, E22/F01/US02/T02); <c>{ consentGiven: false }</c> records the
    /// refusal and hands the verification to an operator, alerted at once. When the biometric verification is not offered (switched off for the
    /// organization, a minor, or no text of the consent approved in production) nothing is recorded and the answer is <c>operator</c>. 400 <c>biometric_consent_answer_required</c> without
    /// <c>consentGiven</c>, 400 <c>biometric_consent_version_required</c> for a consent without its version (at most 50 characters), 422
    /// <c>biometric_consent_version_unknown</c> for a version the registry does not serve now (the page reads the texts again); 404 for an
    /// unknown, expired or completed link. Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default. With
    /// <see cref="FeatureFlags.CheckInIdentitySelfHosted"/> on too (E70/F02/US01/T01) the answer also records the choice of the guest (<c>automatic</c> or
    /// <c>assisted</c>) and opens the identity step when <c>.../identity/open</c> was not called, with the served consent as the notice; the contract is unchanged.
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

        // E70/F02/US01/T01: on the self-hosted flow the answer is also the choice of the guest and may open the identity step, with the family of the browser only.
        var result = SelfHostedIdentityOn()
            ? await verificationFlow.RecordConsentAsync(session, consentGiven, request.ConsentVersion, RequestChannel(), HttpContext.RequestAborted)
            : await verificationFlow.RecordConsentAsync(session, consentGiven, request.ConsentVersion, HttpContext.RequestAborted);
        if (result.Status == BiometricConsentStatus.VersionRequired)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "biometric_consent_version_required", "CheckInBiometricConsentVersionRequired");

        if (result.Status == BiometricConsentStatus.VersionUnknown)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, BiometricConsentVersionUnknownCode, "CheckInBiometricConsentVersionUnknown");

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
    /// <para>
    /// <b>Two sides</b> (E22/F01/US01/T02): the form field <c>side</c> is <c>front</c>, <c>back</c> or <c>single</c> (the default, and today's behaviour: one
    /// picture with the face and the MRZ, as on a passport). The Italian electronic identity card and the other TD-1 cards have the face on the front and the
    /// MRZ on the back: the face must come from the front (or a single picture), the MRZ from any side, and <c>acceptable</c> is true when both were found
    /// across the pictures of the attempt. <c>guidanceCodes</c> then say which side is missing (<c>document_back_needed</c>, <c>document_front_needed</c>);
    /// <c>side</c> and <c>sides</c> in the answer are the side just read and the pictures on file. A side sent again replaces its own picture and is not a
    /// new attempt. 400 <c>document_side_invalid</c> for any other value of <c>side</c>.
    /// </para>
    /// </summary>
    [HttpPost("{token}/document")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    [RequestSizeLimit(MaxImageRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxImageRequestBytes, MultipartBodyLengthLimit = MaxImageRequestBytes)]
    public async Task<ActionResult<DocumentUploadResponse>> UploadDocument(
        string token,
        [FromForm] IFormFile document,
        [FromForm] string? side = null)
    {
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        // E70/F02/US01/T01: a closed self-hosted verification takes no new picture of the document (409 identity_already_closed, with the state).
        if (await RefuseClosedIdentityAsync(session) is { } closed)
            return closed;

        if (session.BiometricOutcome == Casazen.Core.Entities.Enums.BiometricOutcome.Accept)
            return this.ApiProblem(StatusCodes.Status409Conflict, "identity_already_verified", "CheckInIdentityAlreadyVerified");

        if (!DocumentSides.TryParse(side, out var documentSide))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_side_invalid", "CheckInDocumentSideInvalid");

        if (document == null || document.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_required", "CheckInDocumentRequired");

        if (!IsImage(document))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_type_unsupported", "CheckInDocumentTypeUnsupported");

        try
        {
            // Read in memory only (ADR-013-R2): the multipart body is buffered in memory, the service zeroes its copy.
            await using var image = document.OpenReadStream();
            var result = await verificationFlow.SubmitDocumentAsync(session, image, documentSide, HttpContext.RequestAborted);
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
                Side = DocumentSides.CodeOf(result.Side),
                Sides = result.SidesReceived ?? [],
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: a message could carry what was being read.
            logger.LogError("Error processing document upload for session {SessionId} ({ErrorType})", session.Id, ex.GetType().Name);
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "document_processing_failed", "CheckInDocumentProcessingFailed");
        }
    }

    /// <summary>Issues a short-lived, one-time pose challenge after consent and a document ready for comparison.</summary>
    [HttpPost("{token}/selfie-challenge")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<SelfieChallenge>> IssueSelfieChallenge(string token)
    {
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        // E70/F02/US01/T01: a closed self-hosted verification issues no new challenge.
        if (await RefuseClosedIdentityAsync(session) is { } closed)
            return closed;

        var challenge = await verificationFlow.IssueSelfieChallengeAsync(session, HttpContext.RequestAborted);
        if (challenge is null)
            return this.ApiProblem(StatusCodes.Status409Conflict, "selfie_challenge_unavailable", "CheckInSelfieChallengeUnavailable");
        return Ok(challenge);
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
    /// <para>
    /// A document of two sides (E22/F01/US01/T02, <c>side</c> <c>front</c> and <c>back</c> of <c>/document</c>): <c>documentPhoto</c> is the front (the face)
    /// and, for a property in Ticino, which keeps nothing, <c>documentPhotoBack</c> is the back; both are the pictures that were read, or 400
    /// <c>document_photo_required</c> and 409 <c>document_photo_mismatch</c>. Elsewhere neither is needed. 409 <c>document_check_required</c> also while
    /// the face or the MRZ of such a document is still missing, with no attempt spent. Send the frontal <c>selfie</c>, the requested <c>turnedSelfie</c> and
    /// the <c>challengeToken</c> issued by <c>/selfie-challenge</c>. A missing, expired or consumed challenge returns 409 <c>selfie_challenge_required</c>.
    /// </para>
    /// </summary>
    [HttpPost("{token}/selfie")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    [RequestSizeLimit(MaxSelfieRequestBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxSelfieRequestBytes, MultipartBodyLengthLimit = MaxSelfieRequestBytes)]
    public async Task<ActionResult<SelfieUploadResponse>> UploadSelfie(
        string token,
        [FromForm] IFormFile selfie,
        [FromForm] IFormFile? documentPhoto = null,
        [FromForm] IFormFile? documentPhotoBack = null,
        [FromForm] IFormFile? turnedSelfie = null,
        [FromForm] string? challengeToken = null)
    {
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        // E70/F02/US01/T01: a closed self-hosted verification takes no new attempt.
        if (await RefuseClosedIdentityAsync(session) is { } closed)
            return closed;

        if (selfie == null || selfie.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "selfie_required", "CheckInSelfieRequired");

        if (selfie.Length > CheckInDocumentLimits.MaxImageBytes || turnedSelfie?.Length > CheckInDocumentLimits.MaxImageBytes
            || documentPhoto?.Length > CheckInDocumentLimits.MaxImageBytes || documentPhotoBack?.Length > CheckInDocumentLimits.MaxImageBytes)
            return this.ApiProblem(StatusCodes.Status413PayloadTooLarge, "selfie_image_too_large", "CheckInSelfieImageTooLarge");

        if (!IsImage(selfie) || (turnedSelfie is not null && (turnedSelfie.Length == 0 || !IsImage(turnedSelfie))))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "selfie_type_unsupported", "CheckInSelfieTypeUnsupported");

        if (documentPhoto is not null && (documentPhoto.Length == 0 || !IsImage(documentPhoto)))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_photo_type_unsupported", "CheckInDocumentPhotoTypeUnsupported");

        if (documentPhotoBack is not null && (documentPhotoBack.Length == 0 || !IsImage(documentPhotoBack)))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_photo_type_unsupported", "CheckInDocumentPhotoTypeUnsupported");

        SelfieStepResult result;
        try
        {
            // ADR-013-R1: in the request, not in a job. ADR-013-R2: in memory only, zeroed by the agent and the pipeline.
            await using var selfieStream = selfie.OpenReadStream();
            await using var documentStream = documentPhoto?.OpenReadStream();
            await using var documentBackStream = documentPhotoBack?.OpenReadStream();
            await using var turnedStream = turnedSelfie?.OpenReadStream();
            result = await verificationFlow.SubmitChallengedSelfieAsync(session, selfieStream, turnedStream, challengeToken,
                documentStream, documentBackStream, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Error processing selfie upload for session {SessionId} ({ErrorType})", session.Id, ex.GetType().Name);
            return this.ApiProblem(StatusCodes.Status500InternalServerError, "selfie_processing_failed", "CheckInSelfieProcessingFailed");
        }

        return result.Status switch
        {
            SelfieStepStatus.ChallengeRequired =>
                this.ApiProblem(StatusCodes.Status409Conflict, "selfie_challenge_required", "CheckInSelfieChallengeRequired"),
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
                LockCodeHeld = result.LockCodeHeld,
            }),
        };
    }

    /// <summary>
    /// The guest asks for a person (E03/F04/US02/T04, GDPR art. 22, AC-NF51-11): at any step, also after a retry or a failed attempt. The
    /// operators who decide the verifications of the property are alerted at once; a photo of the document kept for the comparison is deleted.
    /// 404 for an unknown, expired or completed link. Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, off by default. Always available, also once
    /// a self-hosted verification is closed; on an opened one it records the switch to the assisted path (E70/F02/US01/T01).
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

    /// <summary>
    /// The tourist tax of the stay of the link (E25/F01/US01/T01, NF-119): what the guest can pay online, the currency, the tax guest by guest by position
    /// (no name, never the reason of an exemption) and the state of the collection. A check-in already completed still reads it. 404 for an unknown or expired
    /// link and for a stay that is not confirmed or in progress (a link reaches its own stay only); 409 <c>tourist_tax_payment_not_available</c> while the
    /// organization does not offer the payment or its account does not take payments. Behind <see cref="FeatureFlags.TouristTaxOnlinePayment"/>.
    /// </summary>
    [HttpGet("{token}/tourist-tax")]
    [FeatureGate(FeatureFlags.TouristTaxOnlinePayment)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<ActionResult<TouristTaxGuestDto>> GetTouristTax(string token)
    {
        if (touristTaxCollection is null)
            return NotFound();

        var view = await touristTaxCollection.GetForGuestAsync(token, HttpContext.RequestAborted);
        return view is null ? NotFound() : Ok(TouristTaxGuestDto.From(view));
    }

    /// <summary>
    /// The PaymentIntent of what the guest can pay online now, on the organization's connected account (direct charge, no fee of CasaZen): one per stay and
    /// amount, kept while it can be paid, with the platform's publishable key and the account for the Payment Element. The payment is recorded by the webhook,
    /// never by this call. 404 like the read; 409 <c>tourist_tax_payment_not_available</c>, <c>tourist_tax_nothing_to_pay</c> or
    /// <c>tourist_tax_payment_in_progress</c>. Behind <see cref="FeatureFlags.TouristTaxOnlinePayment"/>.
    /// </summary>
    [HttpPost("{token}/tourist-tax/payment-intent")]
    [FeatureGate(FeatureFlags.TouristTaxOnlinePayment)]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckInSubmit)]
    public async Task<ActionResult<TouristTaxPaymentIntentDto>> CreateTouristTaxPaymentIntent(string token)
    {
        if (touristTaxCollection is null)
            return NotFound();

        var session = await touristTaxCollection.CreatePaymentIntentAsync(token, HttpContext.RequestAborted);
        return session is null ? NotFound() : Ok(TouristTaxPaymentIntentDto.From(session));
    }

    private static bool IsImage(IFormFile file) =>
        AllowedImageTypes.Contains(file.ContentType.ToLowerInvariant(), StringComparer.Ordinal);
}
