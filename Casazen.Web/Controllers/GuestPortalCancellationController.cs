using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalCancellationController(IGuestBookingCancellationService cancellations)
    : GuestPortalControllerBase
{
    [HttpGet("cancellation")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        if (!Portal.IsDirect)
            return LinkInvalid();

        return Ok(GuestCancellationPreviewDto.From(await cancellations.PreviewForGuestAreaAsync(Portal.BookingId, ct)));
    }

    [HttpPost("cancellation")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> Cancel(
        GuestCancelRequest request, [FromHeader(Name = "Idempotency-Key")] string? key)
    {
        if (!Portal.IsDirect)
            return LinkInvalid();
        if (request.Confirm is not true)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity,
                GuestCancellationErrorCodes.ConfirmationRequired, "GuestCancelConfirmRequired");
        if (!GuestCancellationProblems.IsValidExpectedRefund(request.ExpectedRefundAmount))
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, "refund_amount_invalid", "RefundAmountInvalid");
        if (GuestMessageRules.NormalizeIdempotencyKey(key) is null)
            throw new DomainRuleException("idempotency_key_invalid", "IdempotencyKeyInvalid");

        try
        {
            var result = await cancellations.CancelFromGuestAreaAsync(Portal.BookingId,
                request.ExpectedRefundAmount!.Value, CancellationToken.None);
            return Ok(GuestCancelResponse.From(result));
        }
        catch (GuestRefundChangedException error)
        {
            return this.RefundChanged(error);
        }
    }
}
