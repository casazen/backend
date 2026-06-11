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
    IPropertyAuthorizationService authorizationService,
    ILogger<PaymentsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Payment>>> GetAll()
    {
        logger.LogInformation("Getting all payments");
        var payments = await paymentService.GetAllPaymentsAsync();
        return Ok(payments);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Payment>> GetById(Guid id)
    {
        var payment = await paymentService.GetPaymentAsync(id);
        return payment == null ? NotFound() : Ok(payment);
    }

    [HttpPost]
    public async Task<ActionResult<Payment>> Create([FromBody] Payment payment)
    {
        var created = await paymentService.CreatePaymentAsync(payment);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPost("{id}/process")]
    public async Task<IActionResult> Process(Guid id)
    {
        logger.LogInformation("Processing payment: {PaymentId}", id);
        try
        {
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
    public async Task<IActionResult> Refund(Guid id, [FromQuery] decimal? amount = null)
    {
        try
        {
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
}
