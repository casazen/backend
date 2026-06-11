using System.Security.Claims;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
[Authorize(Policy = "RequireContext:short-rent:property.read")]
public class PropertiesController(
    IPropertyService propertyService,
    IImageStorageService imageStorageService,
    IPropertyAuthorizationService authorizationService,
    IPropertyDocumentService documentService,
    IAdminAccessAuditService adminAccessAuditService,
    IOrgContextResolver orgContextResolver,
    IEntitlementService entitlementService,
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
        var userId = GetAuthenticatedUserId();

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

    /// <summary>
    /// Creates a new property for the authenticated owner.
    /// </summary>
    /// <remarks>
    /// <c>OwnerId</c> is derived from the caller's JWT <c>sub</c> claim and must not be supplied
    /// in the request body. Any client-side attempt to specify <c>OwnerId</c> is ignored.
    /// </remarks>
    /// <param name="request">Property details. See <see cref="CreatePropertyRequest"/> for required fields.</param>
    /// <returns>The newly created property with its assigned <c>Id</c> and <c>OwnerId</c>.</returns>
    /// <response code="201">Property created successfully.</response>
    /// <response code="401">The caller is not authenticated.</response>
    [HttpPost]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    [ProducesResponseType(typeof(Property), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Property>> Create([FromBody] CreatePropertyRequest request)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        // Resolve org; auto-provision Starter org for legacy users who have roles but no OrgId (#217).
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
        {
            logger.LogWarning("Property creation blocked: user {UserId} has no org context", userId);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "No organization context",
                code = "no_org_context"
            });
        }

        // AC8: enforce the org's plan limit server-side before insert. Client-side gating is
        // advisory; this is the source of truth (a stale client cannot exceed the limit).
        if (!await entitlementService.ReservePropertySlotAsync(orgId.Value))
        {
            var entitlement = await entitlementService.GetEntitlementAsync(orgId.Value);
            logger.LogWarning(
                "Property creation blocked by plan limit for org {OrgId} (tier {PlanTier}, limit {Limit})",
                orgId, entitlement.PlanTier, entitlement.MaxProperties);

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Plan limit reached",
                code = "plan_limit_reached",
                planTier = entitlement.PlanTier,
                limit = entitlement.MaxProperties
            });
        }

        logger.LogInformation("Creating property for user: {UserId}", userId);
        var property = request.ToProperty(userId);
        // AC7: tenant key is server-set from the caller's org, never client-supplied.
        property.OrgId = orgId.Value;
        var created = await propertyService.CreatePropertyAsync(property);
        logger.LogInformation("Property created: {PropertyId} in org {OrgId}", created.Id, created.OrgId);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Updates an existing property. Only the property owner may perform this operation.
    /// </summary>
    /// <remarks>
    /// <c>OwnerId</c> is never accepted from the request body; ownership is always verified
    /// against the authenticated caller's JWT <c>sub</c> claim. Sending an <c>OwnerId</c> field
    /// in the body has no effect and will not change the property owner.
    /// </remarks>
    /// <param name="id">The unique identifier of the property to update.</param>
    /// <param name="request">Updated property details. See <see cref="UpdatePropertyRequest"/> for available fields.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Property updated successfully.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not the owner of this property.</response>
    /// <response code="404">No property found with the given <paramref name="id"/>.</response>
    [HttpPut("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePropertyRequest request)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return NotFound();

        var roles = GetUserRoles();
        if (!authorizationService.CanAccess(userId, existing.OwnerId, roles))
        {
            logger.LogWarning("User {UserId} attempted to update property {PropertyId} owned by {OwnerId}",
                userId, id, existing.OwnerId);
            return Forbid();
        }

        await AuditPrivilegedAccessIfNeededAsync(userId, id, existing.OwnerId, roles, "Property.Update");

        request.ApplyTo(existing);
        await propertyService.UpdatePropertyAsync(existing);
        return NoContent();
    }

    [HttpGet("cin-compliance")]
    [Authorize(Policy = "RequireContext:short-rent:property.read")]
    public async Task<ActionResult<CinComplianceResponse>> GetCinCompliance(
        [FromQuery] string? cinStatus,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 200) pageSize = 50;

        try
        {
            var result = await propertyService.GetOwnerCinComplianceAsync(userId, cinStatus, page, pageSize);
            return Ok(new CinComplianceResponse
            {
                Items = result.Items.Select(i => new CinComplianceItemResponse
                {
                    PropertyId = i.PropertyId,
                    PropertyName = i.PropertyName,
                    CinCode = i.CinCode,
                    CinStatus = i.CinStatus,
                    City = i.City,
                }).ToList(),
                TotalCount = result.TotalCount,
                Summary = new CinComplianceSummaryResponse
                {
                    Valid = result.Summary.Valid,
                    Missing = result.Summary.Missing,
                    Invalid = result.Summary.Invalid,
                    DaysUntilDeadline = result.Summary.DaysUntilDeadline,
                    Deadline = result.Summary.Deadline.ToString("yyyy-MM-dd"),
                    HasNonCompliant = result.Summary.HasNonCompliant,
                },
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}/cin")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<IActionResult> UpdateCin(Guid id, [FromBody] UpdatePropertyCinRequest request)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return NotFound();

        var roles = GetUserRoles();
        if (!authorizationService.CanAccess(userId, existing.OwnerId, roles))
            return Forbid();

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        try
        {
            await propertyService.UpdatePropertyCinAsync(id, request.CinCode);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        logger.LogInformation("User {UserId} attempting to delete property: {PropertyId}", userId, id);
        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return NotFound();

        if (!authorizationService.CanAccess(userId, existing.OwnerId, GetUserRoles()))
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
    public async Task<ActionResult<IEnumerable<PublicPropertyDto>>> Search(
        [FromQuery] string? city,
        [FromQuery] int? bedrooms,
        [FromQuery] decimal? maxPrice)
    {
        var properties = await propertyService.SearchAsync(city, bedrooms, maxPrice);
        return Ok(properties);
    }

    [HttpGet("{id}/public")]
    [AllowAnonymous]
    public async Task<ActionResult<PublicPropertyDetailDto>> GetPublic(Guid id)
    {
        var property = await propertyService.GetPublicPropertyAsync(id);
        if (property is null)
            return NotFound();

        return Ok(property);
    }

    // Image Management Endpoints

    [HttpPost("{id}/images")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<ActionResult<Property>> UploadImages(Guid id, [FromForm] List<IFormFile> images)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
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
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<ActionResult<Property>> DeleteImage(Guid id, int imageIndex)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
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
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<ActionResult<Property>> ReorderImages(Guid id, [FromBody] List<string> orderedImageUrls)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
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

    // ─── Detail + Documents ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full detail view of a property, including documents, OTA integrations (no API keys), and bookings summary.
    /// </summary>
    /// <param name="id">The unique identifier of the property.</param>
    /// <returns>A <see cref="PropertyDetailResponse"/> with aggregate data.</returns>
    /// <response code="200">Property detail returned successfully.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not own this property.</response>
    /// <response code="404">No property found with the given <paramref name="id"/>.</response>
    [HttpGet("{id}/detail")]
    public async Task<ActionResult<PropertyDetailResponse>> GetDetail(Guid id)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        PropertyDetailResponse detail;
        try
        {
            detail = await propertyService.GetPropertyDetailAsync(id);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        var roles = GetUserRoles();
        if (!authorizationService.CanAccess(userId, detail.OwnerId, roles))
            return Forbid();

        await AuditPrivilegedAccessIfNeededAsync(userId, id, detail.OwnerId, roles, "PropertyDetail.Read");

        return Ok(detail);
    }

    /// <summary>
    /// Lists all documents attached to a property.
    /// </summary>
    /// <param name="id">The unique identifier of the property.</param>
    /// <returns>Collection of <see cref="PropertyDocumentDto"/>.</returns>
    /// <response code="200">Documents returned successfully.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not own this property.</response>
    /// <response code="404">No property found with the given <paramref name="id"/>.</response>
    [HttpGet("{id}/documents")]
    public async Task<ActionResult<IEnumerable<PropertyDocumentDto>>> GetDocuments(Guid id)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        var roles = GetUserRoles();
        if (!authorizationService.CanAccess(userId, property.OwnerId, roles))
            return Forbid();

        await AuditPrivilegedAccessIfNeededAsync(userId, id, property.OwnerId, roles, "PropertyDocument.List");

        var documents = await documentService.GetByPropertyIdAsync(id);
        return Ok(documents.Select(ToDocumentDto));
    }

    /// <summary>
    /// Uploads a document to a property. Accepts multipart/form-data.
    /// </summary>
    /// <param name="id">The unique identifier of the property.</param>
    /// <param name="file">The document file to upload.</param>
    /// <param name="documentType">The document type (e.g. <c>CinCertificate</c>, <c>FloorPlan</c>).</param>
    /// <returns>The created <see cref="PropertyDocumentDto"/>.</returns>
    /// <response code="201">Document uploaded successfully.</response>
    /// <response code="400">Invalid document type value.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not own this property.</response>
    /// <response code="404">No property found with the given <paramref name="id"/>.</response>
    [HttpPost("{id}/documents")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<ActionResult<PropertyDocumentDto>> UploadDocument(
        Guid id,
        IFormFile file,
        [FromForm] string documentType)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        var roles = GetUserRoles();
        if (!authorizationService.CanAccess(userId, property.OwnerId, roles))
            return Forbid();

        if (!Enum.TryParse<DocumentType>(documentType, ignoreCase: true, out var docType))
            return BadRequest(new { error = $"Invalid document type: {documentType}" });

        await AuditPrivilegedAccessIfNeededAsync(userId, id, property.OwnerId, roles, "PropertyDocument.Upload");

        try
        {
            var document = await documentService.UploadDocumentAsync(id, file, docType, userId);
            return CreatedAtAction(nameof(GetDocuments), new { id }, ToDocumentDto(document));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Invalid document", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a document from a property.
    /// </summary>
    /// <param name="id">The unique identifier of the property.</param>
    /// <param name="docId">The unique identifier of the document to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Document deleted successfully.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not own this property.</response>
    /// <response code="404">Property or document not found.</response>
    [HttpDelete("{id}/documents/{docId}")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<IActionResult> DeleteDocument(Guid id, Guid docId)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        var roles = GetUserRoles();
        if (!authorizationService.CanAccess(userId, property.OwnerId, roles))
            return Forbid();

        var document = await documentService.GetDocumentAsync(docId);
        if (document == null || document.PropertyId != id)
            return NotFound();

        await AuditPrivilegedAccessIfNeededAsync(userId, id, property.OwnerId, roles, "PropertyDocument.Delete");

        await documentService.DeleteDocumentAsync(docId);
        return NoContent();
    }

    private static PropertyDocumentDto ToDocumentDto(PropertyDocument d) =>
        Casazen.Infrastructure.Services.PropertyService.MapDocument(d);

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

    private async Task AuditPrivilegedAccessIfNeededAsync(
        string userId,
        Guid propertyId,
        string ownerId,
        IReadOnlyList<string> roles,
        string action)
    {
        if (userId == ownerId)
            return;

        if (!roles.Any(r => r is "PropertyManager" or "Admin"))
            return;

        await adminAccessAuditService.LogPrivilegedPropertyAccessAsync(userId, propertyId, ownerId, action);
    }
}
