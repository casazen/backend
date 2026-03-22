using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
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
        var success = await paymentService.ProcessPaymentAsync(id);
        if (!success)
        {
            logger.LogWarning("Payment processing failed: {PaymentId}", id);
            return BadRequest("Payment processing failed");
        }

        var payment = await paymentService.GetPaymentAsync(id);
        return Ok(payment);
    }

    [HttpPost("{id}/refund")]
    public async Task<IActionResult> Refund(Guid id, [FromQuery] decimal? amount = null)
    {
        var success = await paymentService.RefundPaymentAsync(id, amount);
        if (!success)
            return BadRequest("Refund processing failed");

        var payment = await paymentService.GetPaymentAsync(id);
        return Ok(payment);
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
