using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs.CheckIn;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
    ILogger<PublicGuestCheckInController> logger) : ControllerBase
{
    /// <summary>
    /// Returns booking context for the guest form. Transitions session Inviato→InCompilazione on first open.
    /// </summary>
    [HttpGet("{token}")]
    [EnableRateLimiting("GuestCheckIn")]
    public async Task<ActionResult<PublicCheckInContextResponse>> GetContext(string token)
    {
        var session = await checkInService.GetSessionByTokenAsync(token);
        if (session is null)
            return NotFound();

        var booking = session.Booking;
        var guest = booking.Guest;

        var response = new PublicCheckInContextResponse
        {
            SessionId = session.Id,
            PropertyName = booking.Property.Name,
            CheckInDate = booking.CheckInDate,
            CheckOutDate = booking.CheckOutDate,
            Status = session.Status.ToString(),
            GuestPrefill = new PublicCheckInGuestPrefill
            {
                FirstName = guest.FirstName,
                LastName = guest.LastName,
                Email = guest.Email,
                DateOfBirth = guest.DateOfBirth,
                Nationality = guest.Nationality,
                Gender = guest.Gender,
                DocumentNumber = guest.DocumentNumber,
                DocumentIssuingCountry = guest.DocumentIssuingCountry,
                PlaceOfBirth = guest.PlaceOfBirth,
            },
        };

        return Ok(response);
    }

    /// <summary>
    /// Accepts guest identity data + GDPR consent. On success enqueues Alloggiati job.
    /// Returns 409 on duplicate submission.
    /// </summary>
    [HttpPost("{token}")]
    [EnableRateLimiting("GuestCheckInSubmit")]
    public async Task<IActionResult> Submit(string token, [FromBody] PublicCheckInSubmitRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (!request.GdprConsent)
            return BadRequest(new { error = "GdprConsentRequired", message = "GDPR consent is required." });

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
            return Conflict(new { error = "AlreadySubmitted", message = "Check-in data was already submitted." });

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
