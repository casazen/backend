using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Payments of the caller's org (TN-3, A3-38). Reads need <c>payment.read</c>, every change <c>payment.write</c>; each
/// payment is then authorized as a <see cref="HostResource"/> of its booking's property (org, permission, ownership).
/// A payment the caller may not see answers 404, like a missing one.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class PaymentsController(
    IPaymentService paymentService,
    IBookingService bookingService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IFiscalRegimeService fiscalRegimeService,
    ILogger<PaymentsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Payment>>> GetAll([FromQuery] Guid? propertyId)
    {
        logger.LogInformation("Getting payments (property filter: {HasProperty})", propertyId.HasValue);

        if (propertyId.HasValue)
        {
            if (!await CanOnPropertyAsync(propertyId.Value, PaymentOperations.Read))
                return NotFound();

            return Ok(await paymentService.GetPropertyPaymentsAsync(propertyId.Value));
        }

        // Org (and ownership) filter applied in SQL: never the whole platform filtered in memory (A3-38).
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null || User.GetHostScope(orgId.Value) is not { } scope)
            return Unauthorized();

        return Ok(await paymentService.GetPaymentsAsync(scope));
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
            return NotFound("Booking not found");

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

    [HttpPost("{id}/process")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    public async Task<IActionResult> Process(Guid id)
    {
        logger.LogInformation("Processing payment: {PaymentId}", id);
        try
        {
            var existing = await paymentService.GetPaymentAsync(id);
            if (existing == null || !await CanOnPaymentAsync(existing, PaymentOperations.Write))
                return NotFound();

            var payment = await paymentService.ProcessPaymentAsync(id);
            return Ok(payment);
        }
        catch (KeyNotFoundException)
        {
            return NotFound($"Payment {id} not found");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Payment processing failed: {PaymentId} - {Error}", id, ex.Message);
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{id}/refund")]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    public async Task<IActionResult> Refund(Guid id, [FromQuery] decimal? amount = null)
    {
        try
        {
            var existing = await paymentService.GetPaymentAsync(id);
            if (existing == null || !await CanOnPaymentAsync(existing, PaymentOperations.Write))
                return NotFound();

            var payment = await paymentService.RefundPaymentAsync(id, amount);
            return Ok(payment);
        }
        catch (KeyNotFoundException)
        {
            return NotFound($"Payment {id} not found");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Refund failed: {PaymentId} - {Error}", id, ex.Message);
            return BadRequest(ex.Message);
        }
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

