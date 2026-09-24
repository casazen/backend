using Casazen.Core.Authorization;
using Casazen.Core.Entities;
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
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ICheckoutReminderScheduler checkoutReminderScheduler,
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
        var movesMoney = request?.RefundAmount > 0 || quote.RequiresRefundDecision || quote.HasUncollectedIntent;
        if (movesMoney && !await authorizationService.IsAuthorizedAsync(User, resource, PaymentOperations.Write))
            return PaymentPermissionMissing();

        var checkoutReminderJobId = booking.CheckoutReminderJobId;
        logger.LogInformation("Cancelling booking {BookingId}", id);
        var result = await cancellationService.CancelAsync(
            new BookingCancellationRequest(id, request?.RefundAmount, request?.Reason, User.GetUserId()),
            HttpContext.RequestAborted);
        checkoutReminderScheduler.CancelReminder(checkoutReminderJobId);

        return Ok(new CancelBookingResponse(
            result.Booking.Id,
            result.Booking.Status,
            result.Refunds.Select(PaymentRefundDto.From).ToList(),
            result.CanceledIntents));
    }

    private async Task<HostResource?> ResourceOfAsync(Booking booking)
    {
        var property = await hostResources.ForPropertyAsync(booking.PropertyId, HttpContext.RequestAborted);
        return property is null ? null : property with { OrgId = booking.OrgId };
    }

    private ObjectResult PaymentPermissionMissing() =>
        this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "BookingCancelPaymentPermissionRequired");
}
