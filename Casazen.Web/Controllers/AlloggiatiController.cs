using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Alloggiati Web communications of the host's bookings (art. 109 TULPS). CasaZen does not transmit to the
/// Questura yet (CO-13): the host sends each schedina on the portal from the per-guest summary and declares it
/// with <c>mark-sent-manually</c> (CO-11, decision D6).
/// </summary>
[ApiController]
[Route("api/alloggiati")]
[Authorize(Policy = "RequireContext:short-rent:booking.read")]
public class AlloggiatiController(
    IAlloggiatiWebService alloggiatiWebService,
    IBookingService bookingService,
    IPropertyAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    ILogger<AlloggiatiController> logger) : ControllerBase
{
    /// <summary>Code of a send request while CasaZen has no Alloggiati Web client (422).</summary>
    public const string TransmissionUnavailableCode = "alloggiati_transmission_unavailable";

    [HttpGet("summary")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    public async Task<ActionResult<IEnumerable<AlloggiatiSummaryDto>>> GetSummary([FromQuery] Guid? propertyId)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        if (propertyId.HasValue && !await CanAccessPropertyAsync(propertyId.Value))
            return Forbid();

        var summaries = await alloggiatiWebService.GetSummaryAsync(orgId.Value, propertyId);
        return Ok(summaries.Select(MapSummary));
    }

    [HttpGet("{bookingId:guid}/status")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    public async Task<ActionResult<AlloggiatiStatusDto>> GetStatus(Guid bookingId)
    {
        if (await AuthorizeBookingAsync(bookingId) is { } denied)
            return denied;

        var status = await alloggiatiWebService.GetStatusAsync(bookingId);
        return Ok(AlloggiatiStatusDto.From(status));
    }

    /// <summary>
    /// Per-guest data to copy on the Alloggiati Web portal, in the order of the record, with the fields still
    /// missing. Contains the guest's identity document: host of the booking only.
    /// </summary>
    [HttpGet("{bookingId:guid}/guest-summary")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    public async Task<ActionResult<AlloggiatiGuestSummaryDto>> GetGuestSummary(Guid bookingId)
    {
        if (await AuthorizeBookingAsync(bookingId) is { } denied)
            return denied;

        var summary = await alloggiatiWebService.GetGuestSummaryAsync(bookingId);
        return Ok(AlloggiatiGuestSummaryDto.From(summary));
    }

    /// <summary>
    /// Records that the host sent the schedina on the Questura portal on <c>sentOn</c>. The status becomes
    /// <c>InviatoManualmente</c> (declared by the host), never <c>Inviato</c>, which needs a real receipt.
    /// </summary>
    [HttpPost("{bookingId:guid}/mark-sent-manually")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<ActionResult<AlloggiatiStatusDto>> MarkSentManually(
        Guid bookingId,
        [FromBody] MarkAlloggiatiSentManuallyRequest request)
    {
        if (await AuthorizeBookingAsync(bookingId) is { } denied)
            return denied;

        var status = await alloggiatiWebService.MarkSentManuallyAsync(bookingId, request.SentOn!.Value);
        logger.LogInformation("Alloggiati communication of booking {BookingId} declared sent manually", bookingId);
        return Ok(AlloggiatiStatusDto.From(status));
    }

    /// <summary>
    /// Transmission to Alloggiati Web is not available (no web service client yet, CO-13): always 422, nothing is
    /// changed. The host sends the schedina on the portal and uses <c>mark-sent-manually</c>.
    /// </summary>
    [HttpPost("{bookingId:guid}/send")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> SendManual(Guid bookingId)
    {
        if (await AuthorizeBookingAsync(bookingId) is { } denied)
            return denied;

        return this.ApiProblem(
            StatusCodes.Status422UnprocessableEntity,
            TransmissionUnavailableCode,
            "AlloggiatiTransmissionUnavailable");
    }

    private async Task<ActionResult?> AuthorizeBookingAsync(Guid bookingId)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return NotFound();

        return await CanAccessPropertyAsync(booking.PropertyId) ? null : Forbid();
    }

    private async Task<bool> CanAccessPropertyAsync(Guid propertyId)
    {
        var userId = GetUserId();
        if (userId is null)
            return false;

        return await authorizationService.CanAccessPropertyAsync(userId, propertyId, GetUserRoles());
    }

    private string? GetUserId() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private IEnumerable<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value);

    private static AlloggiatiSummaryDto MapSummary(AlloggiatiSummaryInfo info) =>
        new()
        {
            BookingId = info.BookingId,
            GuestName = info.GuestName,
            PropertyName = info.PropertyName,
            CheckInDate = info.CheckInDate,
            Status = info.Status,
            DataComplete = info.DataComplete,
            IsOverdue = info.IsOverdue,
            HoursUntilDeadline = info.HoursUntilDeadline,
            DeadlineAt = info.DeadlineAt,
            IsShortStay = info.IsShortStay,
        };
}
