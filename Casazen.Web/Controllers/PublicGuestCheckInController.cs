using Casazen.Core.Entities;
using Casazen.Core.Services;
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
    IStringLocalizer<SharedResources> localizer,
    ILogger<PublicGuestCheckInController> logger) : ControllerBase
{
    /// <summary>Problem code of a submit on a session the guest has already completed (409).</summary>
    public const string AlreadySubmittedCode = "checkin_already_submitted";

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
    /// Accepts the data of every guest of the stay (CO-12) + GDPR consent. On success schedules the Alloggiati job for
    /// the arrival day. Invalid data → 400 ValidationProblem with the errors keyed by request property
    /// (<c>Guests[1].DocumentNumber</c>);
    /// duplicate submission → 409 <c>checkin_already_submitted</c>.
    /// </summary>
    [HttpPost("{token}")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckInSubmit)]
    public async Task<IActionResult> Submit(string token, [FromBody] PublicCheckInSubmitRequest request)
    {
        if (!request.GdprConsent)
            ModelState.AddModelError(nameof(request.GdprConsent), localizer[CheckInValidationKeys.GdprConsentRequired]);

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var ip = ClientIp.GetString(HttpContext) ?? string.Empty;

        var submitRequest = new GuestCheckInSubmitRequest
        {
            Guests = request.Guests.Select(g => g.ToInput()).ToList(),
            GdprConsent = request.GdprConsent,
            MarketingConsent = request.MarketingConsent,
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
}
