using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The payment link of a booking the host entered (NF-74, E08/F01/US01/T05): the guest is e-mailed a secure link to pay the balance online on the
/// host's Stripe account. TN-3: the booking is authorized as a <see cref="HostResource"/> of its property, for <c>booking.write</c> and, because
/// it starts a payment, <c>payment.write</c>; a booking of another org, or one the caller may not reach, answers 404 like a missing one.
/// </summary>
[ApiController]
[Route("api/bookings/{id:guid}/payment-link")]
[Authorize(Policy = CasazenPolicies.BookingWrite)]
public class BookingPaymentLinkController(
    IBookingPaymentLinkService paymentLinks,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<BookingPaymentLinkController> logger) : ControllerBase
{
    /// <summary>
    /// Makes a new payment link of the balance and e-mails it to the guest: the previous link stops working. 200 with the link, given this once
    /// for the host to copy (the token in it is never stored), the amount, the currency and whether the e-mail was queued. 404 for a booking of
    /// another org or out of the caller's reach; 422 <c>payment_link_unavailable</c> when the booking is not one the host entered, is cancelled,
    /// has nothing left to pay or a payment in progress, or the Stripe account of the org is not ready; 503 <c>payment_provider_error</c> when
    /// Stripe did not answer.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [ProducesResponseType(typeof(BookingPaymentLinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingPaymentLinkResponse>> Create(Guid id, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(id, cancellationToken);
        if (resource is null
            || !await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write)
            || !await authorizationService.IsAuthorizedAsync(User, resource, PaymentOperations.Write))
        {
            return NotFound();
        }

        var link = await paymentLinks.CreateAsync(id, cancellationToken);
        logger.LogInformation("Payment link of booking {BookingId} made by user {UserId}", id, User.GetUserId());
        return Ok(BookingPaymentLinkResponse.From(link));
    }
}
