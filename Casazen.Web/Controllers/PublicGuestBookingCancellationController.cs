using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public (no-auth) guest self-service cancellation via a signed one-time link (BK-02, BK-07, PO 2026-10-08).
/// Route: <c>POST /api/public/bookings/{id}/cancel?token={token}</c>
/// </summary>
[ApiController]
[Route("api/public/bookings")]
[AllowAnonymous]
public class PublicGuestBookingCancellationController(
    IGuestBookingCancellationService guestCancellationService,
    ILogger<PublicGuestBookingCancellationController> logger) : ControllerBase
{
    /// <summary>
    /// Verifies the signed one-time token and, if valid, cancels the booking with the refund dictated by the
    /// property's cancellation policy (base = stay price excluding tourist tax, no CasaZen floor). The token is
    /// consumed on success and not usable again. Invalid or expired tokens → 422; booking not found → 404;
    /// concurrent payment in progress → 409.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [EnableRateLimiting(RateLimitPolicies.GuestCheckIn)]
    public async Task<IActionResult> Cancel(Guid id, [FromQuery] string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { code = "guest_cancel_token_missing", message = "Cancel token is required." });

        var result = await guestCancellationService.CancelByGuestLinkAsync(id, token, HttpContext.RequestAborted);

        logger.LogInformation(
            "Guest self-cancellation of booking {BookingId}: refund {RefundAmount} EUR, {RefundCount} Stripe refund(s)",
            id, result.RefundAmount, result.Refunds.Count);

        return Ok(new
        {
            bookingId = result.Booking.Id,
            status = result.Booking.Status.ToString(),
            refundAmount = result.RefundAmount,
        });
    }
}
