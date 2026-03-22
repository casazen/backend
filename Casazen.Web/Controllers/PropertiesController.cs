using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PropertiesController(IPropertyService propertyService, ILogger<PropertiesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Property>>> GetAll()
    {
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var properties = await propertyService.GetOwnerPropertiesAsync(Guid.Parse(userId));
        return Ok(properties);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Property>> GetById(Guid id)
    {
        var property = await propertyService.GetPropertyAsync(id);
        return property == null ? NotFound() : Ok(property);
    }

    [HttpPost]
    public async Task<ActionResult<Property>> Create([FromBody] Property property)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        logger.LogInformation("Creating property for user: {UserId}", userId);
        property.OwnerId = Guid.Parse(userId);
        var created = await propertyService.CreatePropertyAsync(property);
        logger.LogInformation("Property created: {PropertyId}", created.Id);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Property property)
    {
        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return NotFound();

        property.Id = id;
        await propertyService.UpdatePropertyAsync(property);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        logger.LogInformation("Deleting property: {PropertyId}", id);
        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return NotFound();

        await propertyService.DeletePropertyAsync(id);
        logger.LogInformation("Property deleted: {PropertyId}", id);
        return NoContent();
    }

    [HttpGet("search")]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<Property>>> Search(
        [FromQuery] string city,
        [FromQuery] int? bedrooms,
        [FromQuery] decimal? maxPrice)
    {
        var properties = await propertyService.SearchAsync(city, bedrooms, maxPrice);
        return Ok(properties);
    }
}
