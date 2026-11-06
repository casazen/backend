using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The host wizard "Guest check-in" (E03/F01/US01/T04, NF-40, NF-45): the state of the six steps, the guests, the reading of a document photo,
/// the confirmation, and the no-show. The arrival and the check-out keep their endpoints (<see cref="BookingLifecycleController"/>).
/// </summary>
/// <remarks>
/// TN-3, the pattern of <see cref="BookingLifecycleController"/>: the tenant filter hides a booking of another org (404), and a booking the caller
/// may not act on is 404 as well. The guests, the reading and the confirmation need <c>alloggiati.submit</c> on the property, like
/// <c>PUT api/alloggiati/{bookingId}/stay-guests</c>; the state needs <c>booking.read</c>, the no-show <c>booking.write</c>. Field errors are a
/// 400 ValidationProblem keyed <c>Guests[i].Field</c>; domain errors are ProblemDetails with their code.
/// </remarks>
[ApiController]
[Route("api/bookings")]
public class CheckInWizardController(
    ICheckInWizardService wizard,
    IDocumentReadingService documentReading,
    IStayLifecycleService stayLifecycle,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IStringLocalizer<SharedResources> localizer,
    ILogger<CheckInWizardController> logger) : ControllerBase
{
    /// <summary>The largest document photo accepted (10 MB), read in memory only.</summary>
    public const int MaxDocumentBytes = 10_000_000;

    private static readonly string[] DocumentContentTypes = ["image/jpeg", "image/jpg", "image/png"];

    /// <summary>
    /// Everything the six steps show: the booking and its attendance, the guests (document numbers masked), the issues, the tourist tax, the
    /// sender, the Octorate and Alloggiati state, what the host can do and the step to open.
    /// </summary>
    [HttpGet("{bookingId:guid}/checkin-wizard")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(CheckInWizardStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckInWizardStateDto>> GetState(Guid bookingId, CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Read, cancellationToken))
            return BookingNotFound();

        var state = await wizard.GetStateAsync(bookingId, cancellationToken);
        return Ok(CheckInWizardStateDto.From(state, await CanReadGuestsAsync(bookingId, cancellationToken)));
    }

    /// <summary>
    /// Replaces the guests of the stay with the host as author, the exemption codes of the tourist tax included (the input is the whole truth).
    /// Invalid data: 400 keyed <c>Guests[i].Field</c>, nothing saved.
    /// </summary>
    [HttpPut("{bookingId:guid}/checkin-wizard/guests")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [ProducesResponseType(typeof(CheckInWizardStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CheckInWizardStateDto>> SaveGuests(
        Guid bookingId,
        [FromBody] ReplaceStayGuestsRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, AlloggiatiOperations.Submit, cancellationToken))
            return BookingNotFound();
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var result = await wizard.SaveGuestsAsync(
            bookingId, request.Guests.Select(g => g.ToInput()).ToList(), userId, cancellationToken);
        if (!result.Success)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.ModelStateKey(nameof(request.Guests)), localizer[error.MessageKey, error.MessageArgs]);
            return ValidationProblem(ModelState);
        }

        return Ok(CheckInWizardStateDto.From(result.State!, await CanReadGuestsAsync(bookingId, cancellationToken)));
    }

    /// <summary>
    /// Reads the photo of a document (multipart <c>document</c>, JPEG or PNG, at most 10 MB): MRZ first, then OCR, in memory only, never stored
    /// (ADR-013-R6). Closed (404) while <c>Features:CheckInIdentityVerification</c> is off, like the public page: the reading is a placeholder.
    /// </summary>
    [HttpPost("{bookingId:guid}/checkin-wizard/document-reading")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [RequestSizeLimit(MaxDocumentBytes)]
    [RequestFormLimits(MemoryBufferThreshold = MaxDocumentBytes, MultipartBodyLengthLimit = MaxDocumentBytes)]
    [ProducesResponseType(typeof(DocumentReadingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentReadingDto>> ReadDocument(
        Guid bookingId,
        [FromForm] IFormFile? document,
        CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, AlloggiatiOperations.Submit, cancellationToken))
            return BookingNotFound();
        if (document is null || document.Length == 0)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_required", "CheckInDocumentRequired");
        if (!DocumentContentTypes.Contains(document.ContentType?.ToLowerInvariant() ?? string.Empty, StringComparer.Ordinal))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "document_type_unsupported", "CheckInDocumentTypeUnsupported");

        await using var image = document.OpenReadStream();
        var reading = await documentReading.ReadAsync(image, cancellationToken);
        logger.LogInformation(
            "Document read in the check-in wizard of booking {BookingId}: {Method}, review {Review}",
            bookingId, reading.Method, reading.RequiresManualReview);
        return Ok(DocumentReadingDto.From(reading));
    }

    /// <summary>
    /// Confirms the wizard: 422 <c>checkin_not_ready</c> with the blocking issues (<c>issues</c>); otherwise the tourist tax is saved when it is
    /// calculable (never blocking), the guests go to Octorate, and the answer reports each element. No booking status changes, nothing is sent
    /// to the police here.
    /// </summary>
    [HttpPost("{bookingId:guid}/checkin-wizard/confirm")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [ProducesResponseType(typeof(CheckInConfirmationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CheckInConfirmationDto>> Confirm(Guid bookingId, CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, AlloggiatiOperations.Submit, cancellationToken))
            return BookingNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var confirmation = await wizard.ConfirmAsync(bookingId, userId, cancellationToken);
        return Ok(CheckInConfirmationDto.From(confirmation, await CanReadGuestsAsync(bookingId, cancellationToken)));
    }

    /// <summary>
    /// The guests never came (NF-45): a confirmed booking from its check-in day. 409 <c>booking_not_confirmed</c>, 422 <c>no_show_too_early</c>,
    /// 409 <c>no_show_alloggiati_sent</c> (the police communication went: correct it on the portal). Idempotent. A booking on Octorate gets
    /// <c>NO_SHOW</c>; a late arrival registered afterwards clears the mark.
    /// </summary>
    [HttpPost("{bookingId:guid}/no-show")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(NoShowResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NoShowResultDto>> MarkNoShow(Guid bookingId, CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Write, cancellationToken))
            return BookingNotFound();

        return Ok(NoShowResultDto.From(await stayLifecycle.MarkNoShowAsync(bookingId, cancellationToken)));
    }

    /// <summary>TN-3: the booking is visible (tenant filter) and the caller holds <paramref name="operation"/> on its property; otherwise 404.</summary>
    private async Task<bool> CanUseBookingAsync(Guid bookingId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        if (resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation))
            return true;

        logger.LogWarning(
            "User {UserId} denied {Permission} on the check-in wizard of booking {BookingId}", User.GetUserId(), operation.PermissionKey, bookingId);
        return false;
    }

    /// <summary>
    /// Whether the caller holds <c>guest.read</c> on the property of the booking. The reason of a tourist tax exemption can reveal a health
    /// condition (art. 9 GDPR): like the full document numbers it is shown only to them, and is <c>null</c> for the others (the accountant role
    /// holds <c>booking.read</c> without <c>guest.read</c>).
    /// </summary>
    private async Task<bool> CanReadGuestsAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, GuestOperations.Read);
    }

    private ObjectResult BookingNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");
}
