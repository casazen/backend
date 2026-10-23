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
/// Payments of the caller's org (TN-3, A3-38). Reads need <c>payment.read</c>, every change <c>payment.write</c>; each
/// payment is then authorized as a <see cref="HostResource"/> of its booking's property (org, permission, ownership).
/// A payment the caller may not see answers 404, like a missing one. There is no "process payment": a payment is
/// collected by Stripe (checkout and webhooks) or recorded by the host as received outside CasaZen (<see cref="Create"/>,
/// SR-06b), and refunds go to Stripe (BK-02, A9-15).
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
    IManualPaymentService manualPayments,
    IPaymentRefundService refundService,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>Code of a <c>from</c> after <c>to</c>.</summary>
    public const string InvalidRangeCode = "payment_list_invalid_range";

    /// <summary>Header of the key that makes a recorded payment happen once, however many times the request is sent (SR-06b).</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Code of a record sent without the <see cref="IdempotencyKeyHeader"/>.</summary>
    public const string IdempotencyKeyRequiredCode = "idempotency_key_required";

    /// <summary>
    /// The payments the caller sees, newest first, as list rows (SR-03): with the guest and the property of the booking, the
    /// amounts in euros as before and in cents, the method and the status by name. All optional: <c>propertyId</c> and
    /// <c>bookingId</c> narrow it (404 when the property or the booking is not one the caller may read payments of),
    /// <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days, both included) keep the payments settled in the period
    /// (<see cref="PaymentListCriteria"/>). Filtered in SQL by the caller's scope whatever the criteria: a caller who reaches some
    /// properties only never sees the payments of the others. 400 <c>payment_list_invalid_range</c>.
    /// <para>
    /// Without <c>page</c> and <c>pageSize</c> the answer is the array above, all the payments, as it always was. With either of them
    /// (SR-03b) it is one page of the same list in the same order, <c>{ items, totalCount, page, pageSize }</c> like the bookings
    /// (<c>page</c> from 1, 1 when missing; <c>pageSize</c> 20 when missing, at most 100).
    /// </para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PaymentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PagedResultDto<PaymentListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> GetAll(
        [FromQuery] Guid? propertyId,
        [FromQuery] Guid? bookingId = null,
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
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

        var criteria = new PaymentListCriteria(propertyId, bookingId, fromDay, toDay);
        if (page is null && pageSize is null)
        {
            var payments = await paymentList.ListAsync(scope, criteria, HttpContext.RequestAborted);
            return Ok(payments.Select(PaymentListItemDto.From).ToList());
        }

        var paged = await paymentList.ListPageAsync(
            scope, criteria, page ?? 1, pageSize ?? PaymentListPage.DefaultPageSize, HttpContext.RequestAborted);
        return Ok(new PagedResultDto<PaymentListItemDto>
        {
            Items = paged.Items.Select(PaymentListItemDto.From).ToList(),
            TotalCount = paged.TotalCount,
            Page = paged.Page,
            PageSize = paged.PageSize,
        });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Payment>> GetById(Guid id)
    {
        var payment = await paymentService.GetPaymentAsync(id);
        if (payment == null || !await CanOnPaymentAsync(payment, PaymentOperations.Read))
            return NotFound();

        return Ok(payment);
    }

    /// <summary>
    /// Records a payment the host received outside CasaZen (SR-06b): cash or a bank transfer, on a booking of the caller's scope
    /// that is not cancelled and does not come from a portal. It is born <c>Completed</c>, settled on <c>receivedOn</c> (Europe/Rome,
    /// today when missing), and never takes the booking past its total (422 <c>payment_exceeds_balance</c>). The
    /// <c>Idempotency-Key</c> header is required: the same key again answers 200 with the payment already written and writes
    /// nothing. Nothing is sent to the guest or to anyone else. 400 <c>idempotency_key_required</c>.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [ProducesResponseType(typeof(Payment), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(Payment), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<Payment>> Create(
        [FromBody] CreatePaymentRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return this.ApiProblem(StatusCodes.Status400BadRequest, IdempotencyKeyRequiredCode, "PaymentIdempotencyKeyRequired");

        var booking = await bookingService.GetBookingAsync(request.BookingId);
        if (booking == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");

        if (!await CanOnPropertyAsync(booking.PropertyId, PaymentOperations.Write, booking.OrgId))
            return NotFound();

        var result = await manualPayments.RecordAsync(
            new ManualPaymentCommand(
                request.BookingId,
                request.Amount,
                request.Method,
                request.ReceivedOn,
                request.Description,
                idempotencyKey,
                request.ApplyOtaWithholding,
                request.ManualWithholdingTax),
            HttpContext.RequestAborted);

        return result.Created
            ? CreatedAtAction(nameof(GetById), new { id = result.Payment.Id }, result.Payment)
            : Ok(result.Payment);
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

/// <summary>A payment the host received outside CasaZen, to record on a booking (SR-06b).</summary>
/// <param name="BookingId">The booking the money is for.</param>
/// <param name="Amount">In euros, more than zero, two decimals at most, not more than the booking still has to collect.</param>
/// <param name="Method"><c>CashOnArrival</c> or <c>BankTransfer</c>.</param>
/// <param name="Description">A note of the host; optional.</param>
/// <param name="ApplyOtaWithholding">As before; portal stays are refused, so it never applies.</param>
/// <param name="ManualWithholdingTax">A withholding the host states by hand, as before.</param>
/// <param name="ReceivedOn">The day the money came in (<c>yyyy-MM-dd</c>, Europe/Rome), today or before; today when missing.</param>
public record CreatePaymentRequest(
    Guid BookingId,
    decimal Amount,
    PaymentMethod Method,
    string? Description,
    bool? ApplyOtaWithholding,
    decimal? ManualWithholdingTax,
    DateOnly? ReceivedOn = null);

