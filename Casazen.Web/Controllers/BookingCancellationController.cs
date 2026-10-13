using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Payments;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Casazen.Web.Controllers;

/// <summary>
/// Host cancellation of a booking with its money on Stripe (BK-02, #51, A3-05), in place of the old
/// <c>DELETE /api/bookings/{id}</c> that only changed the status. TN-3: the booking is authorized as a
/// <see cref="HostResource"/> of its property; anything that moves money (a refund, or an intent to cancel) also needs
/// <c>payment.write</c> on it, so only the owning host, or an org member with payment write, can do it.
/// </summary>
[ApiController]
[Route("api/bookings/{id:guid}")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class BookingCancellationController(
    IBookingService bookingService,
    IBookingCancellationService cancellationService,
    IGuestBookingCancellationService guestCancellationService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<BookingCancellationController> logger) : ControllerBase
{
    /// <summary>
    /// What cancelling now would do: paid and refundable amounts, the minimum refund of the rule that applies (free
    /// cancellation deadline, property cancellation policy) and whether an unpaid intent would be canceled. Needs
    /// <c>booking.read</c> and <c>payment.read</c>.
    /// </summary>
    [HttpGet("cancellation")]
    public async Task<ActionResult<BookingCancellationQuoteDto>> GetQuote(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        var resource = booking is null ? null : await ResourceOfAsync(booking);
        if (resource is null || !await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Read))
            return NotFound();

        if (!await authorizationService.IsAuthorizedAsync(User, resource, PaymentOperations.Read))
            return PaymentPermissionMissing();

        var quote = await cancellationService.GetQuoteAsync(id, HttpContext.RequestAborted);
        return Ok(BookingCancellationQuoteDto.From(quote));
    }

    /// <summary>
    /// Cancels the booking: unpaid PaymentIntent / SetupIntent canceled on Stripe, <c>refundAmount</c> of what was paid
    /// refunded on the host's connected account. The answer lists the refunds as Stripe left them (succeeded,
    /// pending or failed): a refund is never shown as done before Stripe confirms it.
    /// </summary>
    [HttpPost("cancel")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    public async Task<ActionResult<CancelBookingResponse>> Cancel(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CancelBookingRequest? request = null)
    {
        var booking = await bookingService.GetBookingAsync(id);
        var resource = booking is null ? null : await ResourceOfAsync(booking);
        if (booking is null || resource is null ||
            !await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write))
            return NotFound();

        var quote = await cancellationService.GetQuoteAsync(id, HttpContext.RequestAborted);

        // BK-02 / PO 2026-10-08: when the HOST cancels the booking the guest always receives a 100% refund,
        // regardless of the property's cancellation policy. The host-chosen refund amount from the request body
        // is ignored and replaced with the full refundable amount.
        var hostRefundAmount = quote.RefundableAmount > 0 ? quote.RefundableAmount : request?.RefundAmount;

        var movesMoney = hostRefundAmount > 0 || quote.HasUncollectedIntent;
        if (movesMoney && !await authorizationService.IsAuthorizedAsync(User, resource, PaymentOperations.Write))
            return PaymentPermissionMissing();

        logger.LogInformation("Cancelling booking {BookingId} by host: full refund {RefundAmount}", id, hostRefundAmount);
        var result = await cancellationService.CancelAsync(
            new BookingCancellationRequest(id, hostRefundAmount, request?.Reason, User.GetUserId()),
            HttpContext.RequestAborted);

        return Ok(new CancelBookingResponse(
            result.Booking.Id,
            result.Booking.Status,
            result.Refunds.Select(PaymentRefundDto.From).ToList(),
            result.CanceledIntents));
    }

    /// <summary>
    /// Generates a signed one-time cancellation link and emails it to the guest (BK-02, BK-07, PO 2026-10-08).
    /// Needs <c>booking.write</c> on the booking's property (the host decides when to send the link).
    /// </summary>
    [HttpPost("guest-cancel-link")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    public async Task<IActionResult> SendGuestCancelLink(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        var resource = booking is null ? null : await ResourceOfAsync(booking);
        if (booking is null || resource is null ||
            !await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write))
            return NotFound();

        try
        {
            await guestCancellationService.SendCancelLinkAsync(id, HttpContext.RequestAborted);
            logger.LogInformation("Guest cancel link for booking {BookingId} sent by host", id);
            return Ok();
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
        catch (DomainRuleException ex)
        {
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, ex.Code, ex.MessageKey);
        }
    }

    private async Task<HostResource?> ResourceOfAsync(Booking booking)
    {
        var property = await hostResources.ForPropertyAsync(booking.PropertyId, HttpContext.RequestAborted);
        return property is null ? null : property with { OrgId = booking.OrgId };
    }

    private ObjectResult PaymentPermissionMissing() =>
        this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "BookingCancelPaymentPermissionRequired");
}
