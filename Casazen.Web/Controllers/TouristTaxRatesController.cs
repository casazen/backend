using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/tourist-tax-rates")]
[Authorize]
public class TouristTaxRatesController(
    ITouristTaxService touristTaxService,
    ILogger<TouristTaxRatesController> logger) : ControllerBase
{
    /// <summary>
    /// Get all active tourist tax rates
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TouristTaxRate>>> GetAll()
    {
        var rates = await touristTaxService.GetAllTaxRatesAsync();
        return Ok(rates);
    }

    /// <summary>
    /// Get tourist tax rate by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<TouristTaxRate>> GetById(Guid id)
    {
        var rate = await touristTaxService.GetTaxRateByIdAsync(id);

        if (rate == null)
        {
            logger.LogWarning("Tourist tax rate not found: {Id}", id);
            return NotFound(new { message = $"Tourist tax rate with ID {id} not found" });
        }

        return Ok(rate);
    }

    /// <summary>
    /// Get active tax rate for a specific city
    /// </summary>
    [HttpGet("city/{city}")]
    public async Task<ActionResult<TouristTaxRate>> GetByCity(string city, [FromQuery] DateTime? date = null)
    {
        var effectiveDate = date ?? DateTime.UtcNow;
        var rate = await touristTaxService.GetTaxRateAsync(city, effectiveDate);

        if (rate == null)
            return NotFound($"No tax rate found for city: {city}");

        return Ok(rate);
    }

    /// <summary>
    /// Calculate tourist tax for a booking
    /// </summary>
    [HttpPost("calculate")]
    public async Task<ActionResult<TouristTaxCalculationResponse>> Calculate(
        [FromBody] TouristTaxCalculationRequest request)
    {
        try
        {
            var taxAmount = await touristTaxService.CalculateTouristTaxAsync(
                request.City,
                request.NumberOfAdults,
                request.NumberOfChildren,
                request.CheckInDate,
                request.CheckOutDate);

            var nights = (request.CheckOutDate.Date - request.CheckInDate.Date).Days;

            return Ok(new TouristTaxCalculationResponse
            {
                City = request.City,
                TaxAmount = taxAmount,
                NumberOfAdults = request.NumberOfAdults,
                NumberOfChildren = request.NumberOfChildren,
                Nights = nights,
                CheckInDate = request.CheckInDate,
                CheckOutDate = request.CheckOutDate
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calculating tourist tax for city {City}", request.City);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Create or update tourist tax rate (Admin only)
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<TouristTaxRate>> CreateOrUpdate([FromBody] TouristTaxRate taxRate)
    {
        try
        {
            var result = await touristTaxService.SaveTaxRateAsync(taxRate);
            logger.LogInformation(
                "Tourist tax rate saved for city {City}: €{Rate}/person/night",
                result.City, result.RatePerPersonPerNight);

            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving tourist tax rate for city {City}", taxRate.City);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update tourist tax rate by ID (Admin only)
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<TouristTaxRate>> Update(Guid id, [FromBody] TouristTaxRate taxRate)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var existing = await touristTaxService.GetTaxRateByIdAsync(id);
        if (existing == null)
        {
            logger.LogWarning("Tourist tax rate not found for update: {Id}", id);
            return NotFound(new { message = $"Tourist tax rate with ID {id} not found" });
        }

        try
        {
            taxRate.Id = id; // Ensure ID cannot be changed
            taxRate.UpdatedAt = DateTime.UtcNow;
            var result = await touristTaxService.SaveTaxRateAsync(taxRate);

            logger.LogInformation(
                "Tourist tax rate updated for city {City}: €{Rate}/person/night",
                result.City, result.RatePerPersonPerNight);

            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating tourist tax rate {Id} for city {City}", id, taxRate.City);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Delete tourist tax rate by ID (Admin only)
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Delete(Guid id)
    {
        logger.LogInformation("Deleting tourist tax rate: {Id}", id);

        var existing = await touristTaxService.GetTaxRateByIdAsync(id);
        if (existing == null)
        {
            logger.LogWarning("Tourist tax rate not found for deletion: {Id}", id);
            return NotFound(new { message = $"Tourist tax rate with ID {id} not found" });
        }

        try
        {
            // Soft delete by setting IsActive = false
            existing.IsActive = false;
            existing.UpdatedAt = DateTime.UtcNow;
            await touristTaxService.SaveTaxRateAsync(existing);

            logger.LogInformation("Tourist tax rate soft-deleted: {Id} for city {City}", id, existing.City);
            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting tourist tax rate {Id}", id);
            return StatusCode(500, new { error = "Failed to delete tourist tax rate" });
        }
    }
}

public record TouristTaxCalculationRequest(
    string City,
    int NumberOfAdults,
    int NumberOfChildren,
    DateTime CheckInDate,
    DateTime CheckOutDate);

public record TouristTaxCalculationResponse
{
    public string City { get; init; } = string.Empty;
    public decimal TaxAmount { get; init; }
    public int NumberOfAdults { get; init; }
    public int NumberOfChildren { get; init; }
    public int Nights { get; init; }
    public DateTime CheckInDate { get; init; }
    public DateTime CheckOutDate { get; init; }
}
