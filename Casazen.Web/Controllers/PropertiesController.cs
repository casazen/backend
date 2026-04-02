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

        var properties = await propertyService.GetOwnerPropertiesAsync(userId);
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
        property.OwnerId = userId;
        var created = await propertyService.CreatePropertyAsync(property);
        logger.LogInformation("Property created: {PropertyId}", created.Id);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Property property)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return NotFound();

        // Authorization check: verify ownership
        if (existing.OwnerId != userId)
        {
            logger.LogWarning("User {UserId} attempted to update property {PropertyId} owned by {OwnerId}",
                userId, id, existing.OwnerId);
            return Forbid();
        }

        property.Id = id;
        property.OwnerId = userId; // Ensure OwnerId cannot be changed
        await propertyService.UpdatePropertyAsync(property);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        logger.LogInformation("User {UserId} attempting to delete property: {PropertyId}", userId, id);
        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return NotFound();

        // Authorization check: verify ownership
        if (existing.OwnerId != userId)
        {
            logger.LogWarning("User {UserId} attempted to delete property {PropertyId} owned by {OwnerId}",
                userId, id, existing.OwnerId);
            return Forbid();
        }

        await propertyService.DeletePropertyAsync(id);
        logger.LogInformation("Property deleted: {PropertyId} by user {UserId}", id, userId);
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
