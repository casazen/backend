using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The guest cancelling their own booking from the guest area (DB-08a, decision DB-D3): the guest who proved the booking with its code and
/// email (<c>api/public/guest/session</c>) sees what they would get back and cancels, with no email link in between. Behind the policy
/// <see cref="CasazenPolicies.GuestSession"/>; the booking is the one <see cref="IGuestSessionService.FindBookingAsync"/> finds for the guest
/// of the session, so a booking of another guest or another site answers the same uniform 404 <c>guest_booking_not_found</c> as everywhere in
/// the area, whatever the body says. The preview and the cancellation use the same calculation of the refund, so the amount shown is the
/// amount refunded. Never indexable and never cached. Runbook <c>docs/runbooks/direct-booking.md</c> § 8.5.
/// </summary>
[ApiController]
[Route("api/public/guest/bookings/{code}")]
[Authorize(Policy = CasazenPolicies.GuestSession)]
[PrivateAnswer]
public class GuestCancellationController(
    IGuestSessionService sessions,
    IGuestBookingCancellationService cancellation,
    ILogger<GuestCancellationController> logger) : ControllerBase
{
    /// <summary>
    /// What cancelling the booking would do now: whether the guest can, the refund (the policy's share of the price of the stay and the
    /// whole tourist tax), what stays with the host and why, the days the policy's windows end on, and the cancellation policy. 401 without
    /// a valid session; 404 <c>guest_booking_not_found</c>; 429.
    /// </summary>
    [HttpGet("cancellation")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(GuestCancellationPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestCancellationPreviewDto>> GetPreview(string code, CancellationToken cancellationToken)
    {
        var ticket = User.GetGuestSessionTicket();
        if (ticket is null)
            return Unauthorized();

        var booking = await sessions.FindBookingAsync(ticket, code, cancellationToken);
        var preview = await cancellation.PreviewForGuestAreaAsync(booking.Id, cancellationToken);
        return Ok(GuestCancellationPreviewDto.From(preview));
    }

    /// <summary>
    /// Cancels the booking for the refund the guest was shown: <c>{ confirm: true, expectedRefundAmount }</c> and the header
    /// <c>Idempotency-Key</c> the screen made for it. The refund is worked out again; if it is not <c>expectedRefundAmount</c> (a threshold of
    /// the policy was crossed, a payment changed) nothing is cancelled and the answer is 409 <c>refund_changed</c> with the new
    /// <c>preview</c>. Otherwise the dates are freed and the refund starts on Stripe: it is <c>pending</c> until Stripe confirms it. A booking
    /// that is already cancelled answers 200 with that cancellation's outcome and refunds nothing again, so a repeated click, with the same
    /// key or not, never refunds twice. 422 <c>guest_cancel_stay_started</c> (the stay began: the guest writes to the host),
    /// <c>booking_not_cancellable_by_guest</c>, <c>guest_cancel_confirmation_required</c>, <c>refund_amount_invalid</c>,
    /// <c>idempotency_key_invalid</c>; 409 <c>booking_payment_in_progress</c>; 401; 404 <c>guest_booking_not_found</c>; 429.
    /// </summary>
    [HttpPost("cancel")]
    [EnableRateLimiting(RateLimitPolicies.GuestBookingCancel)]
    [GuestBookingCancelRateLimit]
    [RequestSizeLimit(2048)]
    [ProducesResponseType(typeof(GuestCancelResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Cancel(
        string code,
        [FromBody] GuestCancelRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
    {
        var ticket = User.GetGuestSessionTicket();
        if (ticket is null)
            return Unauthorized();

        // The uniform 404 comes first: nothing in the body can tell a booking of another guest from one that does not exist.
        var booking = await sessions.FindBookingAsync(ticket, code, HttpContext.RequestAborted);

        if (request.Confirm is not true)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity, GuestCancellationErrorCodes.ConfirmationRequired, "GuestCancelConfirmRequired");
        }

        if (!GuestCancellationProblems.IsValidExpectedRefund(request.ExpectedRefundAmount))
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "refund_amount_invalid", "RefundAmountInvalid");

        // The key of this click: a repeated click answers the outcome of the cancellation because the booking is cancelled, and the
        // refund is reserved once; the key keeps the clients honest about retrying the same click. It names nobody.
        if (GuestMessageRules.NormalizeIdempotencyKey(idempotencyKey) is null)
            throw new DomainRuleException("idempotency_key_invalid", "IdempotencyKeyInvalid");

        try
        {
            // Not tied to the request: a guest who closes the page after the booking was cancelled must not leave a refund reserved
            // and never sent to Stripe.
            var result = await cancellation.CancelFromGuestAreaAsync(booking.Id, request.ExpectedRefundAmount!.Value, CancellationToken.None);

            logger.LogInformation(
                "Guest cancellation of booking {BookingId} answered: refund {RefundAmount} EUR, {RefundStatus}",
                booking.Id, result.RefundAmount, result.RefundStatus);
            return Ok(GuestCancelResponse.From(result));
        }
        catch (GuestRefundChangedException ex)
        {
            return this.RefundChanged(ex);
        }
    }
}
