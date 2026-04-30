using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
public class PaymentsController(IPaymentService paymentService, ILogger<PaymentsController> logger) : ControllerBase
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
        var revenue = await paymentService.GetTotalRevenueAsync(propertyId, startDate, endDate);
        return Ok(new { propertyId, startDate, endDate, revenue });
    }
}
