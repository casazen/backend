using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The tenant's payment page of a rent installment (LT-06, #269), reached from the link of the payment request email. The
/// token of the link is the only access check (sent in the body, never in the URL of the API call); a wrong installment
/// id or token gets the same 404 <c>rent_payment_link_invalid</c>. No personal data in the answers. Rate limited per
/// client IP.
/// </summary>
[ApiController]
[Route("api/public/rent-payments")]
[AllowAnonymous]
public class PublicRentPaymentsController(IRentBillingService rentBilling, IBankLinkService bankLinks) : ControllerBase
{
    /// <summary>The installment: property, landlord (org name), period, due date, amount and its real state.</summary>
    [HttpPost("{installmentId:guid}")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(PublicRentPayment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicRentPayment>> Get(
        Guid installmentId, [FromBody] RentPaymentTokenRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        return Ok(await rentBilling.GetPublicPaymentAsync(installmentId, request.Token, cancellationToken));
    }

    /// <summary>
    /// The PaymentIntent to confirm with the Stripe Payment Element on the landlord's connected account: created, or the
    /// current one reused (never two payable at once). 409 <c>rent_installment_not_payable</c> (paid, cancelled, online
    /// payments no longer accepted) or <c>rent_installment_in_flight</c> (a payment is being processed).
    /// </summary>
    [HttpPost("{installmentId:guid}/payment-session")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(PublicRentPaymentSession), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PublicRentPaymentSession>> CreatePaymentSession(
        Guid installmentId, [FromBody] RentPaymentTokenRequest request,
        [FromServices] ITenantRentSepaService sepa, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        var session = await sepa.PayPublicAsync(installmentId, request.Token, cancellationToken);
        return Ok(new PublicRentPaymentSession(installmentId, session.ClientSecret, session.PublishableKey, session.StripeAccountId));
    }

    /// <summary>
    /// Financial Connections session for the tenant of this installment's lease. The token of the payment link is the
    /// access check. No tenant screen is served by the API.
    /// </summary>
    [HttpPost("{installmentId:guid}/bank-link/session")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(BankLinkSession), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankLinkSession>> CreateBankLinkSession(
        Guid installmentId, [FromBody] RentPaymentTokenRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        return Ok(await bankLinks.CreateLeaseSessionAsync(installmentId, request.Token, cancellationToken));
    }

    /// <summary>Stores the tenant's Financial Connections account id on this lease only.</summary>
    [HttpPost("{installmentId:guid}/bank-link")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(BankLinkAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankLinkAccount>> AttachBankLink(
        Guid installmentId, [FromBody] RentBankLinkAccountRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        return Ok(await bankLinks.AttachLeaseAccountAsync(installmentId, request.Token, request.AccountId, cancellationToken));
    }

    /// <summary>
    /// Starts the debit of the tenant's linked account for this installment. The installment stays unpaid until Stripe
    /// confirms, or the landlord records the manual payment.
    /// </summary>
    [HttpPost("{installmentId:guid}/bank-link/payment")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(BankLinkPaymentStarted), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BankLinkPaymentStarted>> StartBankLinkPayment(
        Guid installmentId, [FromBody] RentPaymentTokenRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        return Ok(await bankLinks.StartLeasePaymentAsync(
            installmentId,
            request.Token,
            new BankLinkCaller(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString()),
            cancellationToken));
    }
}

/// <summary>Body of the public rent payment endpoints: the token of the payment link.</summary>
public sealed class RentPaymentTokenRequest
{
    [Required(ErrorMessage = "RentPaymentLinkInvalid")]
    [MaxLength(128, ErrorMessage = "RentPaymentLinkInvalid")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>The tenant's linked account for the lease of this installment.</summary>
public sealed class RentBankLinkAccountRequest
{
    [Required(ErrorMessage = "RentPaymentLinkInvalid")]
    [MaxLength(128, ErrorMessage = "RentPaymentLinkInvalid")]
    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessage = "BankLinkAccountIdInvalid")]
    [MaxLength(255, ErrorMessage = "BankLinkAccountIdInvalid")]
    public string AccountId { get; set; } = string.Empty;
}
