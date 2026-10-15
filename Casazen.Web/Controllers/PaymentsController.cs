using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Payments;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Casazen.Web.Controllers;

/// <summary>
/// Payments of the caller's org (TN-3, A3-38). Reads need <c>payment.read</c>, every change <c>payment.write</c>; each
/// payment is then authorized as a <see cref="HostResource"/> of its booking's property (org, permission, ownership).
/// A payment the caller may not see answers 404, like a missing one. There is no "process payment": a payment is
/// collected only by Stripe (checkout and webhooks), and refunds go to Stripe (BK-02, A9-15).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class PaymentsController(
    IPaymentService paymentService,
    IPaymentListService paymentList,
    IBookingService bookingService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IFiscalRegimeService fiscalRegimeService,
    IPaymentRefundService refundService,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>Code of a <c>from</c> after <c>to</c>.</summary>
    public const string InvalidRangeCode = "payment_list_invalid_range";

    /// <summary>
    /// The payments the caller sees, newest first, as list rows (SR-03): with the guest and the property of the booking, the
    /// amounts in euros as before and in cents, the method and the status by name. All optional: <c>propertyId</c> and
    /// <c>bookingId</c> narrow it (404 when the property or the booking is not one the caller may read payments of),
    /// <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days, both included) keep the payments settled in the period
    /// (<see cref="PaymentListCriteria"/>). Filtered in SQL by the caller's scope whatever the criteria: a caller who reaches some
    /// properties only never sees the payments of the others. 400 <c>payment_list_invalid_range</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PaymentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<PaymentListItemDto>>> GetAll(
        [FromQuery] Guid? propertyId,
        [FromQuery] Guid? bookingId = null,
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null)
    {
        logger.LogInformation(
            "Getting payments (filters: property {HasProperty}, booking {HasBooking}, period {HasPeriod})",
            propertyId.HasValue, bookingId.HasValue, fromDay.HasValue || toDay.HasValue);

        if (fromDay is { } first && toDay is { } last && first > last)
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidRangeCode, "PaymentListInvalidRange");

        if (propertyId.HasValue && !await CanOnPropertyAsync(propertyId.Value, PaymentOperations.Read))
            return NotFound();

        if (bookingId.HasValue)
        {
            var booking = await hostResources.ForBookingAsync(bookingId.Value, HttpContext.RequestAborted);
            if (booking is null || !await authorizationService.IsAuthorizedAsync(User, booking, PaymentOperations.Read))
                return NotFound();
        }

        // Org (and ownership) filter applied in SQL: never the whole platform filtered in memory (A3-38).
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, HttpContext.RequestAborted) is not { } scope)
            return Unauthorized();

        var payments = await paymentList.ListAsync(
            scope, new PaymentListCriteria(propertyId, bookingId, fromDay, toDay), HttpContext.RequestAborted);
        return Ok(payments.Select(PaymentListItemDto.From).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Payment>> GetById(Guid id)
    {
        var payment = await paymentService.GetPaymentAsync(id);
        if (payment == null || !await CanOnPaymentAsync(payment, PaymentOperations.Read))
            return NotFound();

        return Ok(payment);
    }

    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    public async Task<ActionResult<Payment>> Create([FromBody] CreatePaymentRequest request)
    {
        var booking = await bookingService.GetBookingAsync(request.BookingId);
        if (booking == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");

        if (!await CanOnPropertyAsync(booking.PropertyId, PaymentOperations.Write, booking.OrgId))
            return NotFound();

        var payment = new Payment
        {
            BookingId = request.BookingId,
            Amount = request.Amount,
            Method = request.Method,
            Description = request.Description ?? string.Empty,
            OrgId = booking.OrgId,
        };
        await fiscalRegimeService.ApplyWithholdingOnCreateAsync(
            payment, booking, request.ApplyOtaWithholding, request.ManualWithholdingTax);
        var created = await paymentService.CreatePaymentAsync(payment);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Refunds the payment on Stripe (BK-02): <c>Amount</c> or everything still refundable. The answer is the refund as
    /// Stripe left it: Succeeded, Pending (Stripe or a retry still working: poll <c>GET {id}/refunds</c>) or Failed.
    /// The payment shows Refunded / PartiallyRefunded only once Stripe has confirmed.
    /// </summary>
    [HttpPost("{id}/refund")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    public async Task<ActionResult<PaymentRefundDto>> Refund(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefundPaymentRequest? request = null)
    {
        var existing = await paymentService.GetPaymentAsync(id);
        if (existing == null || !await CanOnPaymentAsync(existing, PaymentOperations.Write))
            return NotFound();

        logger.LogInformation("Refund requested for payment {PaymentId}", id);
        var refund = await refundService.RefundAsync(
            new PaymentRefundRequest(id, request?.Amount, request?.Reason, User.GetUserId()),
            HttpContext.RequestAborted);
        return Ok(PaymentRefundDto.From(refund));
    }

    /// <summary>Refunds of the payment (newest first) with the refunded, pending and still refundable amounts.</summary>
    [HttpGet("{id}/refunds")]
    public async Task<ActionResult<PaymentRefundsResponse>> GetRefunds(Guid id)
    {
        var existing = await paymentService.GetPaymentAsync(id);
        if (existing == null || !await CanOnPaymentAsync(existing, PaymentOperations.Read))
            return NotFound();

        var summary = await refundService.GetSummaryAsync(id, HttpContext.RequestAborted);
        var refunds = await refundService.GetRefundsAsync(id, HttpContext.RequestAborted);
        return Ok(PaymentRefundsResponse.From(id, summary, refunds));
    }

    [HttpGet("revenue")]
    public async Task<ActionResult<decimal>> GetRevenue(
        [FromQuery] Guid propertyId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate)
    {
        if (!await CanOnPropertyAsync(propertyId, PaymentOperations.Read))
            return NotFound();

        var revenue = await paymentService.GetTotalRevenueAsync(propertyId, startDate, endDate);
        return Ok(new { propertyId, startDate, endDate, revenue });
    }

    private Task<bool> CanOnPaymentAsync(Payment payment, HostOperationRequirement operation) =>
        payment.Booking is null
            ? Task.FromResult(false)
            : CanOnPropertyAsync(payment.Booking.PropertyId, operation, payment.OrgId);

    /// <summary>
    /// Authorizes <paramref name="operation"/> on a row bound to <paramref name="propertyId"/>. When the row carries
    /// its own org (<paramref name="rowOrgId"/>), that org is the one checked, with the property's owner.
    /// </summary>
    private async Task<bool> CanOnPropertyAsync(Guid propertyId, HostOperationRequirement operation, Guid? rowOrgId = null)
    {
        var property = await hostResources.ForPropertyAsync(propertyId, HttpContext.RequestAborted);
        if (property is null)
            return false;

        var resource = rowOrgId is Guid orgId ? property with { OrgId = orgId } : property;
        return await authorizationService.IsAuthorizedAsync(User, resource, operation);
    }
}

public record CreatePaymentRequest(
    Guid BookingId,
    decimal Amount,
    PaymentMethod Method,
    string? Description,
    bool? ApplyOtaWithholding,
    decimal? ManualWithholdingTax);

