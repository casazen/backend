using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
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
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TouristTaxRate>>> GetAll()
    {
        var rates = await touristTaxService.GetAllTaxRatesAsync();
        return Ok(rates);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TouristTaxRate>> GetById(Guid id)
    {
        var rate = await touristTaxService.GetTaxRateByIdAsync(id);

        if (rate == null)
        {
            logger.LogWarning("Tourist tax rate not found: {Id}", id);
            return this.ApiProblem(
                StatusCodes.Status404NotFound,
                TouristTaxService.TouristTaxRateNotFoundCode,
                "TouristTaxRateNotFound");
        }

        return Ok(rate);
    }

    [HttpGet("city/{city}")]
    public async Task<ActionResult<TouristTaxRate>> GetByCity(string city, [FromQuery] DateTime? date = null)
    {
        var effectiveDate = date ?? DateTime.UtcNow;
        var rate = await touristTaxService.GetTaxRateAsync(city, effectiveDate);

        if (rate == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "TouristTaxRateNotFound");

        return Ok(rate);
    }

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(TouristTaxRate), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TouristTaxRate>> Create(
        [FromBody] TouristTaxRateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await touristTaxService.CreateTaxRateAsync(request.ToInput(), cancellationToken);
        logger.LogInformation(
            "Tourist tax rate {Id} created for city {City}: {Rate}/person/night",
            result.Id, result.City, result.RatePerPersonPerNight);

        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(TouristTaxRate), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TouristTaxRate>> Update(
        Guid id,
        [FromBody] TouristTaxRateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await touristTaxService.UpdateTaxRateAsync(id, request.ToInput(), cancellationToken);
        logger.LogInformation(
            "Tourist tax rate {Id} updated for city {City}: {Rate}/person/night",
            id, result.City, result.RatePerPersonPerNight);

        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await touristTaxService.DeactivateTaxRateAsync(id, cancellationToken);
        logger.LogInformation("Tourist tax rate soft-deleted: {Id}", id);
        return NoContent();
    }
}
