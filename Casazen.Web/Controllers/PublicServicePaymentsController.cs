using Casazen.Core.Services;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The payment page of a service a supplier completed (SP-15a, decision D2), reached from the link of the payment request email:
/// <c>/service/pay/{id}?token=…</c>. The token of the link is the only access check; it is sent in the body, never in the URL of
/// the API call, and a wrong payment id, a wrong token and a link past its validity all get the same 404
/// <c>service_payment_link_invalid</c>. No personal data in the answers. Rate limited per client IP.
/// </summary>
/// <remarks>
/// <b>Not behind the feature flag <c>SupplierOnlinePayments</c></b>: the flag stops the creation of payment requests, while the
/// payments that exist (money in flight) stay payable. The payment itself is a direct charge on the supplier's Stripe account
/// (the payer's browser talks to Stripe.js; CasaZen only creates the PaymentIntent). Runbook <c>docs/runbooks/stripe.md</c> §
/// "Services of the suppliers (SP-15)".
/// </remarks>
[ApiController]
[Route("api/public/service-payments")]
[AllowAnonymous]
public class PublicServicePaymentsController(ISupplierPaymentService payments) : ControllerBase
{
    /// <summary>The payment: who asks for what, the lines of the price and where it stands (<c>Payable</c>, <c>Processing</c>, <c>Paid</c>, <c>Unavailable</c>).</summary>
    [HttpPost("{paymentId:guid}")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(PublicServicePaymentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicServicePaymentDto>> Get(
        Guid paymentId,
        [FromBody] ServicePaymentTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        SetNoStore();
        return Ok(PublicServicePaymentDto.From(await payments.GetPublicAsync(paymentId, request.Token, cancellationToken)));
    }

    /// <summary>
    /// The PaymentIntent to confirm with the Stripe Payment Element on the supplier's connected account: created, or the current
    /// one reused (never two payable at once), under the payment lock. 409 <c>service_payment_not_payable</c> (paid or withdrawn),
    /// <c>service_payment_in_flight</c> (a payment is being processed), <c>service_payment_supplier_not_ready</c> (the supplier cannot
    /// take charges and payouts now).
    /// </summary>
    [HttpPost("{paymentId:guid}/payment-session")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(ServicePaymentSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ServicePaymentSessionDto>> CreatePaymentSession(
        Guid paymentId,
        [FromBody] ServicePaymentTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        SetNoStore();
        return Ok(ServicePaymentSessionDto.From(await payments.CreatePublicSessionAsync(paymentId, request.Token, cancellationToken)));
    }

    /// <summary>The answers carry a state, and the session a client secret: nothing is cached by the browser or a proxy.</summary>
    private void SetNoStore() => Response.Headers.CacheControl = "private, no-store";
}
