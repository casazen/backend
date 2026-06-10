using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Alloggiati;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/alloggiati")]
[Authorize(Policy = "PropertyOwner")]
public class AlloggiatiController(
    IAlloggiatiWebService alloggiatiWebService,
    IBookingService bookingService,
    IPropertyAuthorizationService authorizationService,
    ILogger<AlloggiatiController> logger) : ControllerBase
{
    [HttpGet("summary")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    public async Task<ActionResult<IEnumerable<AlloggiatiSummaryDto>>> GetSummary([FromQuery] Guid? propertyId)
    {
        if (propertyId.HasValue && !await CanAccessPropertyAsync(propertyId.Value))
            return Forbid();

        var summaries = await alloggiatiWebService.GetSummaryAsync(propertyId);
        return Ok(summaries.Select(MapSummary));
    }

    [HttpGet("{bookingId:guid}/status")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    public async Task<ActionResult<AlloggiatiStatusDto>> GetStatus(Guid bookingId)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return NotFound();

        if (!await CanAccessPropertyAsync(booking.PropertyId))
            return Forbid();

        try
        {
            var status = await alloggiatiWebService.GetStatusAsync(bookingId);
            return Ok(MapStatus(status));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{bookingId:guid}/send")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<ActionResult<AlloggiatiStatusDto>> SendManual(Guid bookingId)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return NotFound();

        if (!await CanAccessPropertyAsync(booking.PropertyId))
            return Forbid();

        try
        {
            var status = await alloggiatiWebService.SendManualAsync(bookingId);
            logger.LogInformation("Manual Alloggiati send for booking {BookingId}", bookingId);
            return Ok(MapStatus(status));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
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

    private static AlloggiatiStatusDto MapStatus(AlloggiatiStatusInfo info) =>
        new()
        {
            BookingId = info.BookingId,
            Status = info.Status,
            ConfirmationNumber = info.ConfirmationNumber,
            ErrorMessage = info.ErrorMessage,
            ReportedAt = info.ReportedAt,
            HoursUntilDeadline = info.HoursUntilDeadline,
            IsOverdue = info.IsOverdue,
            DataComplete = info.DataComplete,
        };

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
        };
}
