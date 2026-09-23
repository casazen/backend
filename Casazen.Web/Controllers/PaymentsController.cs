using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
public class PaymentsController(
    IPaymentService paymentService,
    IBookingService bookingService,
    IPropertyAuthorizationService authorizationService,
    IFiscalRegimeService fiscalRegimeService,
    ILogger<PaymentsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Payment>>> GetAll([FromQuery] Guid? propertyId)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        logger.LogInformation("Getting all payments");
        IEnumerable<Payment> payments;

        if (propertyId.HasValue)
        {
            if (!await authorizationService.CanAccessPropertyAsync(userId, propertyId.Value, GetUserRoles()))
                return NotFound();

            payments = await paymentService.GetPropertyPaymentsAsync(propertyId.Value);
        }
        else
        {
            payments = await paymentService.GetAllPaymentsAsync();
            payments = await FilterAccessiblePaymentsAsync(payments, userId);
        }

        return Ok(payments);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Payment>> GetById(Guid id)
    {
        var payment = await paymentService.GetPaymentAsync(id);
        if (payment == null)
            return NotFound();

        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        if (!await CanAccessPaymentAsync(payment, userId))
            return NotFound();

        return Ok(payment);
    }

    [HttpPost]
    [Authorize(Policy = "RequireContext:short-rent:payment.write")]
    public async Task<ActionResult<Payment>> Create([FromBody] CreatePaymentRequest request)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var booking = await bookingService.GetBookingAsync(request.BookingId);
        if (booking == null)
            return NotFound("Booking not found");

        if (!await authorizationService.CanAccessPropertyAsync(userId, booking.PropertyId, GetUserRoles()))
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
    [Authorize(Policy = "RequireContext:short-rent:payment.write")]
    public async Task<IActionResult> Process(Guid id)
    {
        logger.LogInformation("Processing payment: {PaymentId}", id);
        try
        {
            var existing = await paymentService.GetPaymentAsync(id);
            if (existing == null)
                return NotFound($"Payment {id} not found");

            var userId = GetAuthenticatedUserId();
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            if (!await CanAccessPaymentAsync(existing, userId))
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
    [Authorize(Policy = "RequireContext:short-rent:payment.write")]
    public async Task<IActionResult> Refund(Guid id, [FromQuery] decimal? amount = null)
    {
        try
        {
            var existing = await paymentService.GetPaymentAsync(id);
            if (existing == null)
                return NotFound($"Payment {id} not found");

            var userId = GetAuthenticatedUserId();
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            if (!await CanAccessPaymentAsync(existing, userId))
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
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, propertyId, GetUserRoles()))
            return NotFound();

        var revenue = await paymentService.GetTotalRevenueAsync(propertyId, startDate, endDate);
        return Ok(new { propertyId, startDate, endDate, revenue });
    }

    private string? GetAuthenticatedUserId() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    private IReadOnlyList<string> GetUserRoles()
    {
        var auth0Roles = Auth0RolesClaimParser.Parse(
            User.FindAll("https://casazen.app/roles").Select(c => c.Value));
        if (auth0Roles.Count > 0)
            return auth0Roles;

        return User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
    }

    private async Task<bool> CanAccessPaymentAsync(Payment payment, string userId)
    {
        if (payment.Booking is null)
            return false;

        return await authorizationService.CanAccessPropertyAsync(
            userId,
            payment.Booking.PropertyId,
            GetUserRoles());
    }

    private async Task<IReadOnlyList<Payment>> FilterAccessiblePaymentsAsync(IEnumerable<Payment> payments, string userId)
    {
        var roles = GetUserRoles();
        var visible = new List<Payment>();

        foreach (var payment in payments)
        {
            if (payment.Booking is not null &&
                await authorizationService.CanAccessPropertyAsync(userId, payment.Booking.PropertyId, roles))
            {
                visible.Add(payment);
            }
        }

        return visible;
    }
}

public record CreatePaymentRequest(
    Guid BookingId,
    decimal Amount,
    PaymentMethod Method,
    string? Description,
    bool? ApplyOtaWithholding,
    decimal? ManualWithholdingTax);

