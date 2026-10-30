using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public (no-auth) guest self-service cancellation via a signed one-time link (BK-02, BK-07, PO 2026-10-08, DB-08a): the page of the
/// link (<c>/book/{orgSlug}/bookings/{id}/cancel?token=</c>) reads what cancelling would do and then cancels.
/// Routes: <c>GET /api/public/bookings/{id}/cancellation?token={token}</c> and <c>POST /api/public/bookings/{id}/cancel?token={token}</c>.
/// The token is the secret (256 random bits, only its hash is stored); the answers are never indexable and never cached.
/// </summary>
[ApiController]
[Route("api/public/bookings")]
[AllowAnonymous]
public class PublicGuestBookingCancellationController(
    IGuestBookingCancellationService guestCancellationService,
    ILogger<PublicGuestBookingCancellationController> logger) : ControllerBase
{
    /// <summary>
    /// What cancelling the booking would do now (DB-08a): the same answer as the preview of the guest area, for the page of the link. The
    /// token is checked like the cancellation checks it but <b>not spent</b>; a booking already cancelled answers 200 with
    /// <c>cancellable: false</c> and the outcome of its cancellation. Missing token → 400; invalid or expired → 422; booking not found → 404.
    /// </summary>
    [HttpGet("{id:guid}/cancellation")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(GuestCancellationPreviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPreview(Guid id, [FromQuery] string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "guest_cancel_token_missing", "GuestCancelTokenMissing");

        var preview = await guestCancellationService.PreviewByGuestLinkAsync(id, token, HttpContext.RequestAborted);
        return Ok(GuestCancellationPreviewDto.From(preview));
    }

    /// <summary>
    /// Verifies the signed one-time token and, if valid, cancels the booking with the refund dictated by the
    /// property's cancellation policy (the policy's share of the stay price plus the tourist tax, no CasaZen floor). The link is spent by
    /// the cancellation that succeeds; an error leaves it usable until it expires. A booking already cancelled (by the
    /// host, or by an earlier or simultaneous click of the same link) answers 200 with that cancellation's outcome,
    /// however many times the link is used, and refunds nothing again. Invalid or expired tokens → 422; booking not
    /// found → 404; concurrent payment in progress → 409.
    /// </summary>
    /// <remarks>
    /// The page of the link sends the optional body <c>{ expectedRefundAmount }</c>, the amount it showed: if the refund is no longer that
    /// (a threshold of the policy was crossed) nothing is cancelled, the link stays usable and the answer is 409 <c>refund_changed</c> with
    /// the new <c>preview</c>. Without a body the link cancels as it always did.
    /// </remarks>
    [HttpPost("{id:guid}/cancel")]
    [EnableRateLimiting(RateLimitPolicies.GuestBookingCancel)]
    [GuestBookingCancelRateLimit]
    [PrivateAnswer]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromQuery] string? token,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] GuestLinkCancelRequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "guest_cancel_token_missing", "GuestCancelTokenMissing");

        var expected = request?.ExpectedRefundAmount;
        if (expected is not null && !GuestCancellationProblems.IsValidExpectedRefund(expected))
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "refund_amount_invalid", "RefundAmountInvalid");

        try
        {
            // Not tied to the request: a guest who closes the page after the booking was cancelled must not leave a refund reserved
            // and never sent to Stripe.
            var result = expected is { } shown
                ? await guestCancellationService.CancelByGuestLinkAsync(id, token, shown, CancellationToken.None)
                : await guestCancellationService.CancelByGuestLinkAsync(id, token, CancellationToken.None);

            logger.LogInformation(
                "Guest self-cancellation of booking {BookingId}: refund {RefundAmount} EUR, {RefundCount} Stripe refund(s)",
                id, result.RefundAmount, result.Refunds.Count);

            return Ok(GuestLinkCancelResponse.From(result));
        }
        catch (GuestRefundChangedException ex)
        {
            return this.RefundChanged(ex);
        }
    }
}
