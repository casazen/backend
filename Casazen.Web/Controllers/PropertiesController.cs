using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
public class PropertiesController(
    IPropertyService propertyService,
    IImageStorageService imageStorageService,
    ILogger<PropertiesController> logger) : ControllerBase
{
    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult HealthCheck()
    {
        logger.LogInformation("Health check called - backend is working!");
        return Ok(new { status = "healthy", message = "Backend is running", timestamp = DateTime.UtcNow });
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Property>>> GetAll()
    {
        logger.LogInformation("GetAll properties called");
        logger.LogInformation($"User authenticated: {User.Identity?.IsAuthenticated}");
        logger.LogInformation($"User identity name: {User.Identity?.Name}");

        // Try multiple claim types to find user ID
        var userId = User.FindFirst("sub")?.Value
                     ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        logger.LogInformation($"User ID from claims: {userId}");

        // DEBUG: Log all claims
        foreach (var claim in User.Claims)
        {
            logger.LogInformation($"Claim: {claim.Type} = {claim.Value}");
        }

        if (string.IsNullOrEmpty(userId))
        {
            logger.LogWarning("No user ID claim found in token");
            return Unauthorized();
        }

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
        [FromQuery] string? city,
        [FromQuery] int? bedrooms,
        [FromQuery] decimal? maxPrice)
    {
        var properties = await propertyService.SearchAsync(city, bedrooms, maxPrice);
        return Ok(properties);
    }

    // Image Management Endpoints

    [HttpPost("{id}/images")]
    public async Task<ActionResult<Property>> UploadImages(Guid id, [FromForm] List<IFormFile> images)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        // Verify ownership
        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        if (property.OwnerId != userId)
        {
            logger.LogWarning("User {UserId} attempted to upload images to property {PropertyId} owned by {OwnerId}",
                userId, id, property.OwnerId);
            return Forbid();
        }

        // Validate maximum image limit (20 images)
        const int maxImages = 20;
        if (property.PhotoUrls.Count + images.Count > maxImages)
        {
            return BadRequest(new { error = $"Maximum {maxImages} images allowed per property. Current: {property.PhotoUrls.Count}, Attempting to add: {images.Count}" });
        }

        // Validate and upload each image
        var uploadedUrls = new List<string>();
        foreach (var image in images)
        {
            if (!imageStorageService.ValidateImage(image))
            {
                logger.LogWarning("Invalid image file rejected: {FileName}", image.FileName);
                return BadRequest(new { error = $"Invalid image file: {image.FileName}. Allowed formats: JPEG, PNG, WebP. Max size: 10MB" });
            }

            try
            {
                var url = await imageStorageService.UploadImageAsync(image, id);
                await propertyService.AddImageAsync(id, url);
                uploadedUrls.Add(url);
                logger.LogInformation("Image uploaded for property {PropertyId}: {ImageUrl}", id, url);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload image for property {PropertyId}", id);
                return StatusCode(500, new { error = "Failed to upload image" });
            }
        }

        // Return updated property
        var updatedProperty = await propertyService.GetPropertyAsync(id);
        return Ok(new { property = updatedProperty, uploadedImages = uploadedUrls });
    }

    [HttpGet("{id}/images")]
    public async Task<ActionResult<List<string>>> GetImages(Guid id)
    {
        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        return Ok(property.PhotoUrls);
    }

    [HttpDelete("{id}/images/{imageIndex}")]
    public async Task<ActionResult<Property>> DeleteImage(Guid id, int imageIndex)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        // Verify ownership
        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        if (property.OwnerId != userId)
        {
            logger.LogWarning("User {UserId} attempted to delete image from property {PropertyId} owned by {OwnerId}",
                userId, id, property.OwnerId);
            return Forbid();
        }

        // Validate image index
        if (imageIndex < 0 || imageIndex >= property.PhotoUrls.Count)
        {
            return BadRequest(new { error = $"Invalid image index {imageIndex}. Property has {property.PhotoUrls.Count} images." });
        }

        try
        {
            // Get the image URL before removing it
            var imageUrl = property.PhotoUrls[imageIndex];

            // Remove from property
            var updatedProperty = await propertyService.RemoveImageAsync(id, imageIndex);

            // Delete from storage
            await imageStorageService.DeleteImageAsync(imageUrl);

            logger.LogInformation("Image deleted from property {PropertyId} at index {Index}", id, imageIndex);
            return Ok(updatedProperty);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(new { error = $"Invalid image index {imageIndex}" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete image from property {PropertyId}", id);
            return StatusCode(500, new { error = "Failed to delete image" });
        }
    }

    [HttpPut("{id}/images/order")]
    public async Task<ActionResult<Property>> ReorderImages(Guid id, [FromBody] List<string> orderedImageUrls)
    {
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        // Verify ownership
        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        if (property.OwnerId != userId)
        {
            logger.LogWarning("User {UserId} attempted to reorder images for property {PropertyId} owned by {OwnerId}",
                userId, id, property.OwnerId);
            return Forbid();
        }

        try
        {
            var updatedProperty = await propertyService.ReorderImagesAsync(id, orderedImageUrls);
            logger.LogInformation("Images reordered for property {PropertyId}", id);
            return Ok(updatedProperty);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reorder images for property {PropertyId}", id);
            return StatusCode(500, new { error = "Failed to reorder images" });
        }
    }
}
