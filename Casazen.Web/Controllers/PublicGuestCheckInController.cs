using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Hangfire;
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
    IBackgroundJobClient backgroundJobClient,
    IStringLocalizer<SharedResources> localizer,
    ILogger<PublicGuestCheckInController> logger) : ControllerBase
{
    /// <summary>Problem code of a submit on a session the guest has already completed (409).</summary>
    public const string AlreadySubmittedCode = "checkin_already_submitted";

    /// <summary>
    /// Returns booking context for the guest form. Transitions session Inviato→InCompilazione on first open.
    /// Before completion the guest prefill has a masked document number; after completion only the status is
    /// returned (no booking data, no PII), so the guest still sees that the check-in is done (A5-28).
    /// </summary>
    [HttpGet("{token}")]
    [EnableRateLimiting("GuestCheckIn")]
    public async Task<ActionResult<PublicCheckInContextResponse>> GetContext(string token)
    {
        var view = await checkInService.GetPublicViewAsync(token);
        if (view is null)
            return NotFound();

        if (view.IsCompleted)
            return Ok(new PublicCheckInContextResponse { Completed = true, Status = view.Status.ToString() });

        var prefill = view.GuestPrefill;
        var response = new PublicCheckInContextResponse
        {
            Completed = false,
            Status = view.Status.ToString(),
            SessionId = view.SessionId,
            PropertyName = view.PropertyName,
            CheckInDate = view.CheckInDate,
            CheckOutDate = view.CheckOutDate,
            GuestPrefill = prefill is null ? null : new PublicCheckInGuestPrefill
            {
                FirstName = prefill.FirstName,
                LastName = prefill.LastName,
                Email = prefill.Email,
                DateOfBirth = prefill.DateOfBirth,
                Nationality = prefill.Nationality,
                Gender = prefill.Gender,
                DocumentNumberMasked = prefill.DocumentNumberMasked,
                DocumentIssuingCountry = prefill.DocumentIssuingCountry,
                PlaceOfBirth = prefill.PlaceOfBirth,
            },
        };

        return Ok(response);
    }

    /// <summary>
    /// Accepts guest identity data + GDPR consent. On success enqueues Alloggiati job.
    /// Invalid data → 400 ValidationProblem with the errors keyed by request property;
    /// duplicate submission → 409 <c>checkin_already_submitted</c>.
    /// </summary>
    [HttpPost("{token}")]
    [EnableRateLimiting("GuestCheckInSubmit")]
    public async Task<IActionResult> Submit(string token, [FromBody] PublicCheckInSubmitRequest request)
    {
        if (!request.GdprConsent)
            ModelState.AddModelError(nameof(request.GdprConsent), localizer[CheckInValidationKeys.GdprConsentRequired]);

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

        var submitRequest = new GuestCheckInSubmitRequest
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            DateOfBirth = request.DateOfBirth,
            Nationality = request.Nationality,
            Gender = request.Gender,
            DocumentType = request.DocumentType,
            DocumentNumber = request.DocumentNumber,
            DocumentIssuingCountry = request.DocumentIssuingCountry,
            PlaceOfBirth = request.PlaceOfBirth,
            GdprConsent = request.GdprConsent,
            MarketingConsent = request.MarketingConsent,
            ConsentIpAddress = ip,
        };

        var result = await checkInService.SubmitAsync(token, submitRequest);

        if (result.Duplicate)
            return this.ApiProblem(StatusCodes.Status409Conflict, AlreadySubmittedCode, "CheckInAlreadySubmitted");

        if (result.ValidationField is not null && result.ValidationErrorKey is not null)
        {
            ModelState.AddModelError(result.ValidationField, localizer[result.ValidationErrorKey]);
            return ValidationProblem(ModelState);
        }

        if (!result.Success)
            return NotFound();

        // Enqueue Alloggiati Web report (mandatory within 24h of arrival, D.L. 286/1998)
        if (result.GuestId.HasValue && result.BookingId.HasValue)
        {
            backgroundJobClient.Enqueue<AlloggiatiWebReportJob>(
                job => job.ReportGuestAsync(result.GuestId.Value, result.BookingId.Value));

            if (result.SessionId.HasValue)
                await checkInService.MarkAlloggiatiEnqueuedAsync(result.SessionId.Value);
        }

        logger.LogInformation(
            "Public check-in submitted for booking {BookingId}, session {SessionId}",
            result.BookingId, result.SessionId);

        return Ok(new { sessionId = result.SessionId, message = "Check-in completed." });
    }
}
