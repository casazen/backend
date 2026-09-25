using System.Globalization;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Properties. The property core (list, record, create/update, documents such as the APE) is shared by short-rent hosts
/// and long-term landlords (<see cref="CasazenPolicies.SharedPropertyRead"/>, A7-06); everything about short stays
/// (photos, CIN, iCal calendars, listing activation, detail with bookings and OTA) stays short-rent only
/// (<see cref="CasazenPolicies.PropertyRead"/>). Shared actions authorize the row with <see cref="SharedPropertyOperations"/>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
public class PropertiesController(
    IPropertyService propertyService,
    IImageStorageService imageStorageService,
    IPropertyAuthorizationService authorizationService,
    ILeaseContractRepository leaseContractRepository,
    IPropertyDocumentService documentService,
    IAdminAccessAuditService adminAccessAuditService,
    IOrgContextResolver orgContextResolver,
    IEntitlementService entitlementService,
    PropertyICalSyncService propertyICalSyncService,
    IComplianceWizardService complianceWizardService,
    IAuthorizationService hostAuthorizationService,
    ILogger<PropertiesController> logger) : ControllerBase
{
    /// <summary>
    /// Properties of the caller's org the caller may handle (TN-3): every one for an org-wide role, otherwise the ones
    /// they own, filtered in SQL. Shared by short-rent hosts and long-term landlords (A7-06).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Property>>> GetAll()
    {
        // Never log the claims or the identity name: they carry email and name (FD-17, A2-32).
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
        {
            logger.LogWarning("No user ID claim found in token");
            return Unauthorized();
        }

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null || User.GetHostScope(orgId.Value) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var properties = await propertyService.GetPropertiesAsync(scope);
        return Ok(properties);
    }

    /// <summary>
    /// The record of one property (<see cref="PropertyResponse"/>): no bookings (nor their check-in tokens), OTA
    /// integrations or documents (A2-32). Another org's property is 404 (tenant filter).
    /// </summary>
    /// <response code="200">The property record.</response>
    /// <response code="403">The caller may not read this property (TN-3).</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PropertyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyResponse>> GetById(Guid id)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyRecordAsync(id);
        if (property == null)
            return NotFound();

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), SharedPropertyOperations.Read))
            return Forbid();

        await AuditPrivilegedAccessIfNeededAsync(userId, id, property.OwnerId, GetUserRoles(), "Property.Read");

        return Ok(PropertyResponse.From(property));
    }

    /// <summary>
    /// The cancellation policies a short-stay property can reference (<see cref="UpdatePropertyRequest.CancellationPolicyId"/>),
    /// by name. The catalog is global, not per org.
    /// </summary>
    [HttpGet("cancellation-policies")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(IReadOnlyList<CancellationPolicyOptionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CancellationPolicyOptionDto>>> GetCancellationPolicies()
    {
        return Ok(await propertyService.GetCancellationPoliciesAsync());
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
    /// <response code="403"><c>plan_limit_reached</c>: the org already has as many properties as its plan allows.</response>
    /// <response code="409">Duplicate active address or slug within the organization.</response>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    [ProducesResponseType(typeof(Property), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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

        logger.LogInformation("Creating property for user: {UserId}", userId);
        var property = request.ToProperty(userId);
        // AC7: tenant key is server-set from the caller's org, never client-supplied.
        property.OrgId = orgId.Value;
        try
        {
            // AC8: the org's plan limit is enforced server-side, and the check and the insert are one atomic
            // step (A1-21): parallel creates cannot exceed the limit. Client-side gating is advisory.
            var created = await entitlementService.CreatePropertyWithinLimitAsync(
                orgId.Value,
                () => propertyService.CreatePropertyAsync(property),
                HttpContext.RequestAborted);
            if (created is null)
            {
                var entitlement = await entitlementService.GetEntitlementAsync(orgId.Value, HttpContext.RequestAborted);
                logger.LogWarning(
                    "Property creation blocked by plan limit for org {OrgId} (tier {PlanTier}, limit {Limit})",
                    orgId, entitlement.PlanTier, entitlement.MaxProperties);

                return this.ApiProblem(StatusCodes.Status403Forbidden, PlanLimitReachedCode, "PlanLimitReached");
            }

            logger.LogInformation("Property created: {PropertyId} in org {OrgId}", created.Id, created.OrgId);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Property creation conflict for user {UserId}", userId);
            return Conflict(new { error = ex.Message, code = "duplicate_property_slug" });
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            logger.LogWarning(ex, "Property creation unique constraint for user {UserId}", userId);
            return Conflict(new
            {
                error = "Indirizzo già usato da un immobile attivo",
                code = "duplicate_property_address"
            });
        }
    }

    /// <summary>
    /// Updates a property with <b>PATCH semantics</b> (A2-04): only the fields present in the body change, a field left
    /// out (or null) keeps its stored value; the nullable CIN, slug and cancellation policy are cleared by sending null.
    /// The web forms send every field they show, so both a partial and a complete body are safe.
    /// </summary>
    /// <remarks>
    /// <c>OwnerId</c> and <c>OrgId</c> are never accepted from the request body. The row is authorized with
    /// <see cref="SharedPropertyOperations.Write"/> (TN-3); another org's property is 404.
    /// </remarks>
    /// <param name="id">The unique identifier of the property to update.</param>
    /// <param name="request">The fields to change. See <see cref="UpdatePropertyRequest"/>.</param>
    /// <response code="204">Property updated.</response>
    /// <response code="400"><c>validation_error</c>: a field sent is not valid (errors by field).</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="409">Slug already used in the org, or city change after a canone concordato registration.</response>
    /// <response code="422"><c>cancellation_policy_not_found</c>: the cancellation policy does not exist.</response>
    [HttpPut("{id}")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePropertyRequest request)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        // The row alone: saving it must not write back the bookings or OTA integrations of the property (A2-04).
        var existing = await propertyService.GetPropertyRecordAsync(id);
        if (existing == null)
            return NotFound();

        var roles = GetUserRoles();
        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(existing), SharedPropertyOperations.Write))
        {
            logger.LogWarning("User {UserId} attempted to update property {PropertyId} owned by {OwnerId}",
                userId, id, existing.OwnerId);
            return Forbid();
        }

        await AuditPrivilegedAccessIfNeededAsync(userId, id, existing.OwnerId, roles, "Property.Update");

        if (request.City is { } city && IsCityChange(existing.City, city) && await HasSubmittedCanoneConcordatoLeaseAsync(id))
        {
            return Conflict(new
            {
                message = "Property city cannot be changed after a canone concordato lease has been submitted for registration."
            });
        }

        request.ApplyTo(existing);
        await propertyService.UpdatePropertyAsync(existing);
        return NoContent();
    }

    private async Task<bool> HasSubmittedCanoneConcordatoLeaseAsync(Guid propertyId)
    {
        var leases = await leaseContractRepository.GetByPropertyAsync(propertyId);
        return leases.Any(lease =>
            lease.FiscalRegime == FiscalRegime.CanoneConcordato &&
            lease.Status is LeaseStatus.RegistrationPending or LeaseStatus.SentToProvider or LeaseStatus.Registered);
    }

    private static bool IsCityChange(string currentCity, string requestedCity) =>
        !string.Equals(currentCity.Trim(), requestedCity.Trim(), StringComparison.OrdinalIgnoreCase);

    [HttpGet("cin-compliance")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
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
                    DaysUntilDeadline = result.Summary.Deadline.DaysUntilDeadline,
                    Deadline = result.Summary.Deadline.Deadline?.ToString(CinOptions.DateFormat, CultureInfo.InvariantCulture),
                    DeadlineStatus = result.Summary.Deadline.PhaseApiValue,
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
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
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

        // Invalid format (422 invalid_cin_format) and CIN already used by another property (409 duplicate_cin)
        // are domain exceptions turned into ProblemDetails by the error middleware.
        await propertyService.UpdatePropertyCinAsync(id, request.CinCode);
        return NoContent();
    }

    /// <summary>
    /// Cadastral identification of the unit (LT-10): sheet (foglio), parcel (particella), subaltern, category and income
    /// (rendita). Used by the lease contract and, for the sheet, to find the canone concordato zone. Shared by
    /// short-rent hosts and long-term landlords. Only lengths are validated.
    /// </summary>
    /// <response code="204">Saved.</response>
    /// <response code="403">The caller may not edit this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpPut("{id:guid}/cadastral")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    public async Task<IActionResult> UpdateCadastral(Guid id, [FromBody] UpdatePropertyCadastralRequest request)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(existing), SharedPropertyOperations.Write))
            return Forbid();

        await AuditPrivilegedAccessIfNeededAsync(userId, id, existing.OwnerId, GetUserRoles(), "Property.UpdateCadastral");

        await propertyService.UpdateCadastralDataAsync(id, new PropertyCadastralData(
            request.Sheet, request.Parcel, request.Subaltern, request.Category, request.Income));
        return NoContent();
    }

    /// <summary>
    /// Soft-deletes the property (PC-05, A2-18): the row is kept (<c>IsDeleted</c>/<c>DeletedAt</c>), never removed, so
    /// its historical bookings and fiscal data (tourist tax, CIN, cedolare secca) stay intact for the Italian
    /// compliance retention; it just stops appearing in every normal read (the property lists, the plan's used slots).
    /// Refused with 409 <c>property_has_upcoming_bookings</c> while a confirmed or checked-in stay has not checked out
    /// yet: a property with a guest already booked cannot simply disappear.
    /// </summary>
    /// <response code="204">Property soft-deleted.</response>
    /// <response code="403">The caller may not delete this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="409"><c>property_has_upcoming_bookings</c>: a confirmed or checked-in stay has not checked out yet.</response>
    [HttpDelete("{id}")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        logger.LogInformation("User {UserId} attempting to delete property: {PropertyId}", userId, id);
        var existing = await propertyService.GetPropertyAsync(id);
        if (existing == null)
            return NotFound();

        var roles = GetUserRoles();
        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(existing), SharedPropertyOperations.Write))
        {
            logger.LogWarning("User {UserId} attempted to delete property {PropertyId} owned by {OwnerId}",
                userId, id, existing.OwnerId);
            return Forbid();
        }

        await AuditPrivilegedAccessIfNeededAsync(userId, id, existing.OwnerId, roles, "Property.Delete");

        // 409 property_has_upcoming_bookings turned into ProblemDetails by the error middleware (FD-05).
        await propertyService.DeletePropertyAsync(id);
        logger.LogInformation("Property soft-deleted: {PropertyId} by user {UserId}", id, userId);
        return NoContent();
    }

    [HttpGet("search")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
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
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
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
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
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
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    public async Task<ActionResult<List<string>>> GetImages(Guid id)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
        {
            logger.LogWarning("User {UserId} attempted to view images for property {PropertyId} owned by {OwnerId}",
                userId, id, property.OwnerId);
            return Forbid();
        }

        return Ok(property.PhotoUrls);
    }

    [HttpDelete("{id}/images/{imageIndex}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
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
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
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
    /// <response code="403">The caller may not read this property (TN-3).</response>
    /// <response code="404">No property found with the given <paramref name="id"/> in the caller's org. Any other
    /// failure is a 500, never a 404 (A2-36).</response>
    [HttpGet("{id}/detail")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(PropertyDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyDetailResponse>> GetDetail(Guid id)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        // TN-3: the row is checked before its bookings and documents are read; another org's property is 404.
        var property = await propertyService.GetPropertyRecordAsync(id);
        if (property == null)
            return NotFound();

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), PropertyOperations.Read))
            return Forbid();

        // A property that disappears in between answers 404 (NotFoundException, FD-05).
        var detail = await propertyService.GetPropertyDetailAsync(id);

        await AuditPrivilegedAccessIfNeededAsync(userId, id, detail.OwnerId, GetUserRoles(), "PropertyDetail.Read");

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

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), SharedPropertyOperations.Read))
            return Forbid();

        await AuditPrivilegedAccessIfNeededAsync(userId, id, property.OwnerId, GetUserRoles(), "PropertyDocument.List");

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
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
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

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), SharedPropertyOperations.Write))
            return Forbid();

        if (!Enum.TryParse<DocumentType>(documentType, ignoreCase: true, out var docType))
            return BadRequest(new { error = $"Invalid document type: {documentType}" });

        await AuditPrivilegedAccessIfNeededAsync(userId, id, property.OwnerId, GetUserRoles(), "PropertyDocument.Upload");

        try
        {
            var document = await documentService.UploadDocumentAsync(id, file, docType, userId);
            return CreatedAtAction(nameof(GetDocuments), new { id }, ToDocumentDto(document));
        }
        catch (ApeComplianceException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        catch (InvalidOperationException ex)
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
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    public async Task<IActionResult> DeleteDocument(Guid id, Guid docId)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return NotFound();

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), SharedPropertyOperations.Write))
            return Forbid();

        var document = await documentService.GetDocumentAsync(docId);
        if (document == null || document.PropertyId != id)
            return NotFound();

        await AuditPrivilegedAccessIfNeededAsync(userId, id, property.OwnerId, GetUserRoles(), "PropertyDocument.Delete");

        await documentService.DeleteDocumentAsync(docId);
        return NoContent();
    }

    /// <summary>
    /// Code and energy class printed on an APE document (LT-10): the lease contract states them (template data). 422
    /// <c>document_not_ape</c> for another document type, <c>ape_identification_invalid</c> for an empty code or a class
    /// that is not 1-3 letters, digits or "+".
    /// </summary>
    /// <response code="200">The updated document.</response>
    /// <response code="403">The caller may not edit this property.</response>
    /// <response code="404">Property or document not found.</response>
    [HttpPut("{id:guid}/documents/{docId:guid}/ape")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    public async Task<ActionResult<PropertyDocumentDto>> UpdateApeIdentification(
        Guid id, Guid docId, [FromBody] UpdateApeIdentificationRequest request)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(id);
        if (property == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), SharedPropertyOperations.Write))
            return Forbid();

        var document = await documentService.GetDocumentAsync(docId);
        if (document == null || document.PropertyId != id)
            return NotFound();

        await AuditPrivilegedAccessIfNeededAsync(userId, id, property.OwnerId, GetUserRoles(), "PropertyDocument.UpdateApe");

        var updated = await documentService.UpdateApeIdentificationAsync(document, request.Code, request.EnergyClass);
        return Ok(ToDocumentDto(updated));
    }

    /// <summary>
    /// Downloads a property document. Documents live in the private bucket: this authenticated endpoint
    /// (tenant filter + ownership) is the only way to read them (FD-07, A2-03/A2-31).
    /// </summary>
    /// <param name="id">The unique identifier of the property.</param>
    /// <param name="docId">The unique identifier of the document.</param>
    /// <response code="200">The file, as an attachment.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not own this property.</response>
    /// <response code="404">Property or document not found (also for another org's property), or file missing from the storage.</response>
    [HttpGet("{id:guid}/documents/{docId:guid}/download")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadDocument(Guid id, Guid docId)
    {
        var access = await AuthorizeDocumentAccessAsync(id, docId, "PropertyDocument.Download");
        if (access.Denied is not null)
            return access.Denied;

        var content = await documentService.OpenContentAsync(access.Document!);
        if (content is null)
        {
            logger.LogWarning("Stored file missing for document {DocumentId} of property {PropertyId}", docId, id);
            return this.ApiProblem(StatusCodes.Status404NotFound, StorageProblemCodes.DocumentFileMissing, "DocumentFileMissing");
        }

        Response.Headers.CacheControl = "private, no-store";
        return File(content, StorageKeys.ContentTypeFor(access.Document!.FileName), access.Document.FileName);
    }

    /// <summary>
    /// Returns a short-lived signed URL of a property document (private bucket), for clients that
    /// download directly from the storage. Same authorization as <see cref="DownloadDocument"/>.
    /// </summary>
    /// <param name="id">The unique identifier of the property.</param>
    /// <param name="docId">The unique identifier of the document.</param>
    /// <response code="200"><c>{ url, expiresAt }</c>.</response>
    /// <response code="403">The caller does not own this property.</response>
    /// <response code="404">Property or document not found (also for another org's property).</response>
    /// <response code="501">The configured storage cannot sign URLs (filesystem provider in Development): use the download endpoint.</response>
    [HttpGet("{id:guid}/documents/{docId:guid}/signed-url")]
    [ProducesResponseType(typeof(SignedDocumentUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult<SignedDocumentUrlResponse>> GetDocumentSignedUrl(Guid id, Guid docId)
    {
        var access = await AuthorizeDocumentAccessAsync(id, docId, "PropertyDocument.SignedUrl");
        if (access.Denied is not null)
            return access.Denied;

        var signed = await documentService.GetSignedDownloadUrlAsync(access.Document!);
        if (signed is null)
        {
            return this.ApiProblem(StatusCodes.Status501NotImplemented, StorageProblemCodes.SignedUrlUnavailable, "SignedUrlUnavailable");
        }

        return Ok(new SignedDocumentUrlResponse(signed.Url.ToString(), signed.ExpiresAtUtc));
    }

    private async Task<(PropertyDocument? Document, ActionResult? Denied)> AuthorizeDocumentAccessAsync(
        Guid propertyId, Guid documentId, string auditAction)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return (null, Unauthorized());

        // Tenant query filter: another org's property is invisible here → 404, never its file.
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null)
            return (null, NotFound());

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), SharedPropertyOperations.Read))
        {
            logger.LogWarning("User {UserId} denied access to document {DocumentId} of property {PropertyId}",
                userId, documentId, propertyId);
            return (null, Forbid());
        }

        var document = await documentService.GetDocumentAsync(documentId);
        if (document == null || document.PropertyId != propertyId)
            return (null, NotFound());

        await AuditPrivilegedAccessIfNeededAsync(userId, propertyId, property.OwnerId, GetUserRoles(), auditAction);
        return (document, null);
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

        if (!roles.Any(Casazen.Core.Authorization.HostRoles.OrgWide.Contains))
            return;

        await adminAccessAuditService.LogPrivilegedPropertyAccessAsync(userId, propertyId, ownerId, action);
    }

    // ─── iCal calendars (PC-11: many import feeds per property, URL encrypted) ─────
    // TN-3 on every action: PropertyRead / PropertyWrite policies plus the resource check of the property (404 when
    // it is not visible, e.g. another org; 403 when visible but the operation is not allowed).

    /// <summary>Export link, block count and import feeds of the property.</summary>
    [HttpGet("{id:guid}/ical/status")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(PropertyIcalStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyIcalStatusDto>> GetIcalStatus(
        Guid id,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeIcalAsync(id, PropertyOperations.Read, hostAuthorization);
        if (denied is not null)
            return denied;

        var export = await propertyICalSyncService.GetOrCreateExportAsync(id, property.OrgId, cancellationToken);
        return Ok(new PropertyIcalStatusDto
        {
            ExportUrl = propertyICalSyncService.BuildExportUrl(export.ExportToken),
            BlockCount = await propertyICalSyncService.GetBlockCountAsync(id, cancellationToken),
            Feeds = await BuildIcalFeedDtosAsync(id, localizer, cancellationToken),
        });
    }

    /// <summary>Import feeds of the property, oldest first; each with its own sync state and a masked URL.</summary>
    [HttpGet("{id:guid}/ical/feeds")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(IReadOnlyList<PropertyIcalFeedDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PropertyIcalFeedDto>>> GetIcalFeeds(
        Guid id,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeIcalAsync(id, PropertyOperations.Read, hostAuthorization);
        if (denied is not null)
            return denied;

        return Ok(await BuildIcalFeedDtosAsync(id, localizer, cancellationToken));
    }

    /// <summary>
    /// Adds an import feed (Airbnb, Booking.com, other) and queues its first sync (FD-16, A2-21): the download runs in
    /// a Hangfire job, never in this request. 202 with status <c>Syncing</c>. 400 <c>ical_invalid_url</c> (not an
    /// external https URL) or <c>ical_feed_invalid_label</c>; 409 <c>ical_feed_duplicate</c>; 422
    /// <c>ical_feed_limit_reached</c>.
    /// </summary>
    [HttpPost("{id:guid}/ical/feeds")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(PropertyIcalFeedDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PropertyIcalFeedDto>> AddIcalFeed(
        Guid id,
        [FromBody] PropertyIcalFeedCreateRequest request,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromServices] IBackgroundJobClient backgroundJobClient,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeIcalAsync(id, PropertyOperations.Write, hostAuthorization);
        if (denied is not null)
            return denied;

        PropertyICalFeed feed;
        try
        {
            feed = await propertyICalSyncService.AddFeedAsync(
                id, property.OrgId, request.Channel, request.Label, request.ImportUrl, cancellationToken);
        }
        catch (DomainRuleException ex) when (ex.Code is ICalErrorCodes.InvalidUrl or ICalFeedErrorCodes.InvalidLabel)
        {
            // Malformed input, not a business rule: 400 like the other validation errors.
            return this.ApiProblem(StatusCodes.Status400BadRequest, ex.Code, ex.MessageKey, [.. ex.MessageArgs]);
        }

        QueueIcalFeedSync(feed.Id, backgroundJobClient);
        return Accepted(BuildIcalFeedDto(feed, blockCount: 0, localizer));
    }

    /// <summary>Removes an import feed and the blocks it imported (only those): 204; 404 <c>ical_feed_not_found</c>.</summary>
    [HttpDelete("{id:guid}/ical/feeds/{feedId:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveIcalFeed(
        Guid id,
        Guid feedId,
        [FromServices] IAuthorizationService hostAuthorization,
        CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeIcalAsync(id, PropertyOperations.Write, hostAuthorization);
        if (denied is not null)
            return denied;

        await propertyICalSyncService.RemoveFeedAsync(id, feedId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// "Sync now" of one import feed: 202 with status <c>Syncing</c> and a queued job, or the current state when a sync
    /// is already queued; 404 <c>ical_feed_not_found</c>.
    /// </summary>
    [HttpPost("{id:guid}/ical/feeds/{feedId:guid}/sync")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(PropertyIcalFeedDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyIcalFeedDto>> SyncIcalFeed(
        Guid id,
        Guid feedId,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromServices] IBackgroundJobClient backgroundJobClient,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeIcalAsync(id, PropertyOperations.Write, hostAuthorization);
        if (denied is not null)
            return denied;

        var (feed, queue) = await propertyICalSyncService.RequestSyncAsync(id, feedId, cancellationToken);
        if (queue)
            QueueIcalFeedSync(feed.Id, backgroundJobClient);

        var counts = await propertyICalSyncService.GetBlockCountsByFeedAsync(id, cancellationToken);
        return Accepted(BuildIcalFeedDto(feed, counts.GetValueOrDefault(feed.Id), localizer));
    }

    [HttpGet("{id:guid}/ical/export-url")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(PropertyIcalExportUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyIcalExportUrlDto>> GetIcalExportUrl(
        Guid id,
        [FromServices] IAuthorizationService hostAuthorization,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeIcalAsync(id, PropertyOperations.Read, hostAuthorization);
        if (denied is not null)
            return denied;

        var export = await propertyICalSyncService.GetOrCreateExportAsync(id, property.OrgId, cancellationToken);
        return Ok(new PropertyIcalExportUrlDto
        {
            ExportUrl = propertyICalSyncService.BuildExportUrl(export.ExportToken),
        });
    }

    /// <summary>
    /// Replaces the token of the export link (PC-12, A2-22): 200 with the new <c>exportUrl</c>; the old link answers
    /// 404 from now on, so the host must paste the new one on every OTA. 404 when the property is not visible (other
    /// org), 403 without write permission on it.
    /// </summary>
    [HttpPost("{id:guid}/ical/export-url/regenerate")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(PropertyIcalExportUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyIcalExportUrlDto>> RegenerateIcalExportUrl(
        Guid id,
        [FromServices] IAuthorizationService hostAuthorization,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeIcalAsync(id, PropertyOperations.Write, hostAuthorization);
        if (denied is not null)
            return denied;

        var export = await propertyICalSyncService.RegenerateExportTokenAsync(id, property.OrgId, cancellationToken);
        return Ok(new PropertyIcalExportUrlDto
        {
            ExportUrl = propertyICalSyncService.BuildExportUrl(export.ExportToken),
        });
    }

    // TN-3 resource-based check of the property for the iCal actions: 404 when the property is not visible (other
    // org), 403 when visible but the operation is not allowed.
    private async Task<(Property Property, ActionResult? Denied)> AuthorizeIcalAsync(
        Guid propertyId,
        HostOperationRequirement operation,
        IAuthorizationService hostAuthorization)
    {
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property is null)
            return (null!, NotFound());

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on iCal of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return (property, Forbid());
        }

        return (property, null);
    }

    private void QueueIcalFeedSync(Guid feedId, IBackgroundJobClient backgroundJobClient)
    {
        try
        {
            backgroundJobClient.Enqueue<PropertyICalSyncJob>(job => job.SyncFeedAsync(feedId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            // The feed is saved: the recurring property-ical-sync job (every 15 minutes) syncs it anyway.
            logger.LogError(ex, "Could not queue the sync of iCal feed {FeedId}", feedId);
        }
    }

    private async Task<IReadOnlyList<PropertyIcalFeedDto>> BuildIcalFeedDtosAsync(
        Guid propertyId,
        IStringLocalizer localizer,
        CancellationToken cancellationToken)
    {
        var feeds = await propertyICalSyncService.ListFeedsAsync(propertyId, cancellationToken);
        var counts = await propertyICalSyncService.GetBlockCountsByFeedAsync(propertyId, cancellationToken);
        return feeds.Select(f => BuildIcalFeedDto(f, counts.GetValueOrDefault(f.Id), localizer)).ToList();
    }

    private static PropertyIcalFeedDto BuildIcalFeedDto(PropertyICalFeed feed, int blockCount, IStringLocalizer localizer)
    {
        var (lastErrorCode, lastErrorMessage) = ICalErrorMessages.Describe(feed.LastError, localizer);
        return new PropertyIcalFeedDto
        {
            Id = feed.Id,
            Channel = feed.Channel.ToString(),
            Label = feed.Label,
            MaskedImportUrl = ICalFeedUrlMask.Mask(feed.ImportUrl),
            CreatedAt = feed.CreatedAt,
            LastImportAt = feed.LastImportAt,
            LastImportStatus = feed.LastImportStatus?.ToString(),
            LastErrorCode = lastErrorCode,
            LastError = lastErrorMessage,
            BlockCount = blockCount,
        };
    }

    // ─── Compliance activation wizard (#295) ───────────────────────────────────

    [HttpGet("{id:guid}/compliance/activation")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(PropertyActivationWizardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyActivationWizardDto>> GetComplianceActivation(
        Guid id,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeShortStayPropertyAsync(id, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        try
        {
            var (loaded, steps) = await complianceWizardService.GetActivationWizardAsync(id, cancellationToken);
            var suspended = loaded.ComplianceStatus == PropertyComplianceStatus.Suspended;
            return Ok(new PropertyActivationWizardDto
            {
                ComplianceStatus = loaded.ComplianceStatus.ToString(),
                SuspendedAt = suspended ? loaded.ComplianceSuspendedAt : null,
                SuspensionReasons = suspended ? loaded.ComplianceSuspensionReasons ?? [] : [],
                Steps = steps.Select(s => ToActivationStepDto(s, localizer)),
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private static ComplianceActivationStepDto ToActivationStepDto(
        ComplianceActivationStep step,
        IStringLocalizer<SharedResources> localizer) => new()
        {
            Id = step.Id,
            Label = step.Label,
            Status = step.Status,
            Blocker = step.Blocker,
            Message = step.MessageKey is null
                ? step.Message
                : localizer[step.MessageKey, step.MessageArgs?.ToArray() ?? []].Value,
            LinkUrl = step.LinkUrl,
            Blockers = step.Blockers.Select(b => ToBlockerDto(step.Id, b, localizer)).ToList(),
            TouristTax = step.TouristTax is not { } tax
                ? null
                : new ActivationTouristTaxDto
                {
                    City = tax.City,
                    PublicPageSlug = tax.PublicPageSlug,
                    CategoryRequired = tax.CategoryRequired,
                    Rate = tax.Rate is not { } rate
                        ? null
                        : new ActivationTouristTaxRateDto
                        {
                            CalculationMethod = rate.CalculationMethod,
                            RatePerPersonPerNight = rate.RatePerPersonPerNight,
                            PercentOfNightlyPrice = rate.PercentOfNightlyPrice,
                            CapPerPersonPerNight = rate.CapPerPersonPerNight,
                            MaxNights = rate.MaxNights,
                            MinimumAge = rate.MinimumAge,
                            ReducedRateMaxAge = rate.ReducedRateMaxAge,
                            ReducedRatePerPersonPerNight = rate.ReducedRatePerPersonPerNight,
                            SeasonStart = rate.SeasonStart,
                            SeasonEnd = rate.SeasonEnd,
                            EffectiveFrom = rate.EffectiveFrom,
                            EffectiveTo = rate.EffectiveTo,
                            SourceUrl = rate.SourceUrl,
                            VerificationLevel = rate.VerificationLevel,
                        },
                },
        };

    private static ActivationBlockerDto ToBlockerDto(
        string stepId,
        ActivationBlocker blocker,
        IStringLocalizer<SharedResources> localizer) => new()
        {
            Step = stepId,
            Code = blocker.Code,
            Message = localizer[blocker.MessageKey, blocker.MessageArgs.ToArray()].Value,
        };

    /// <summary>Code of the 409 of <see cref="CompleteComplianceActivation"/> when blocking steps are left.</summary>
    internal const string ActivationBlockedCode = "property_activation_blocked";

    /// <summary>
    /// Activates the property when every blocking step is complete. 409 <c>property_activation_blocked</c> otherwise, with
    /// <c>incompleteBlockers</c> (step ids) and <c>blockers</c> (<c>{ step, code, message }</c>, stable codes such as
    /// <c>safety_gas_detector_missing</c>); 409 <c>activation_tos_required</c> without the terms accepted. The safety
    /// checklist is saved with <see cref="SaveSafetyChecklist"/> (CO-07). Same evaluation as the re-evaluation after a
    /// change (CO-06): with blockers left an active property is suspended and a pending or suspended one keeps its status
    /// (<c>complianceStatus</c> of the 409).
    /// </summary>
    [HttpPost("{id:guid}/compliance/activation/complete")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(CompletePropertyActivationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CompletePropertyActivationResponse>> CompleteComplianceActivation(
        Guid id,
        [FromBody] CompletePropertyActivationRequest request,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var denied = await AuthorizeShortStayPropertyAsync(id, PropertyOperations.Write);
        if (denied is not null)
            return denied;

        try
        {
            var (updated, blockingSteps) = await complianceWizardService.CompleteActivationAsync(
                id, userId, request.TosAccepted, cancellationToken);

            if (blockingSteps.Count > 0)
            {
                var problem = ApiProblemDetails.Create(
                    HttpContext, StatusCodes.Status409Conflict, ActivationBlockedCode, "PropertyActivationBlocked");
                problem.Extensions["complianceStatus"] = updated.ComplianceStatus.ToString();
                problem.Extensions["incompleteBlockers"] = blockingSteps.Select(s => s.Id).ToList();
                problem.Extensions["blockers"] = blockingSteps
                    .SelectMany(s => s.Blockers.Select(b => ToBlockerDto(s.Id, b, localizer)))
                    .ToList();
                return new ObjectResult(problem)
                {
                    StatusCode = StatusCodes.Status409Conflict,
                    ContentTypes = { ApiProblemDetails.ContentType },
                };
            }

            return Ok(new CompletePropertyActivationResponse
            {
                ComplianceStatus = updated.ComplianceStatus.ToString(),
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // ─── D.L. 145/2023 safety checklist (CO-07) ────────────────────────────────

    /// <summary>
    /// Safety checklist of the property (D.L. 145/2023 art. 13-ter): facts, answers, items "not applicable" with their
    /// reason, minimum extinguishers, blockers and warnings with stable codes. An empty checklist before the first save.
    /// </summary>
    [HttpGet("{id:guid}/compliance/safety-checklist")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(SafetyChecklistDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SafetyChecklistDto>> GetSafetyChecklist(
        Guid id,
        [FromServices] IPropertySafetyChecklistService safetyChecklistService,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeShortStayPropertyAsync(id, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var view = await safetyChecklistService.GetAsync(id, cancellationToken);
        return Ok(ToSafetyChecklistDto(view, localizer));
    }

    /// <summary>
    /// Saves the whole safety checklist (facts and answers). "Not applicable" is not sent: it follows from the facts.
    /// <c>confirm</c> records the host's final confirmation (SC-08) of these answers; any later save without it clears
    /// it. 422 with a stable code (<c>safety_*</c>) for invalid values; the evidence must be a document of the property.
    /// </summary>
    [HttpPut("{id:guid}/compliance/safety-checklist")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(SafetyChecklistDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SafetyChecklistDto>> SaveSafetyChecklist(
        Guid id,
        [FromBody] SaveSafetyChecklistRequest request,
        [FromServices] IPropertySafetyChecklistService safetyChecklistService,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var denied = await AuthorizeShortStayPropertyAsync(id, PropertyOperations.Write);
        if (denied is not null)
            return denied;

        var facts = request.Facts ?? new SafetyChecklistFactsDto();
        var input = new SafetyChecklistInput(
            new SafetyChecklistFactsInput(
                facts.Entrepreneurial,
                facts.HasGasSupply,
                facts.CombustionAppliances,
                facts.FloorCount,
                facts.FloorAreasSqm),
            (request.Items ?? []).Select(i => new SafetyChecklistItemInput(
                i.Code,
                i.Answer,
                i.Quantity,
                i.Location,
                i.DetectorType,
                i.CheckedOn,
                i.ExpiresOn,
                i.EvidenceDocumentId,
                i.Notes)).ToList(),
            request.Confirm);

        var view = await safetyChecklistService.SaveAsync(id, userId, input, cancellationToken);
        return Ok(ToSafetyChecklistDto(view, localizer));
    }

    private static SafetyChecklistDto ToSafetyChecklistDto(SafetyChecklistView view, IStringLocalizer<SharedResources> localizer)
    {
        var checklist = view.Checklist;
        var answers = checklist?.Items.ToDictionary(i => i.Code) ?? new Dictionary<SafetyItemCode, PropertySafetyChecklistItem>();

        return new SafetyChecklistDto
        {
            SchemaVersion = checklist?.SchemaVersion ?? SafetyChecklistRules.SchemaVersion,
            LegalBasis = checklist?.LegalBasis is { Length: > 0 } basis ? basis : SafetyChecklistRules.LegalBasis,
            DeclarationTextVersion = SafetyChecklistRules.DeclarationTextVersion,
            Saved = checklist is not null,
            ImportedFromLegacy = checklist is { SchemaVersion: SafetyChecklistRules.LegacySchemaVersion },
            Facts = new SafetyChecklistFactsDto
            {
                Entrepreneurial = checklist?.Entrepreneurial,
                HasGasSupply = checklist?.HasGasSupply,
                CombustionAppliances = checklist?.CombustionAppliances,
                FloorCount = checklist?.FloorCount,
                FloorAreasSqm = checklist?.FloorAreasSqm,
            },
            Items = view.Evaluation.Items.Select(e =>
            {
                answers.TryGetValue(e.Code, out var item);
                return new SafetyChecklistItemDto
                {
                    Code = e.Code,
                    Requirement = e.Requirement,
                    Status = e.Status,
                    NotApplicableReason = e.NotApplicableReason,
                    Answer = item?.Answer,
                    Quantity = item?.Quantity,
                    Location = item?.Location,
                    DetectorType = item?.DetectorType,
                    CheckedOn = item?.CheckedOn,
                    ExpiresOn = item?.ExpiresOn,
                    EvidenceDocumentId = item?.EvidenceDocumentId,
                    EvidenceFileName = item?.EvidenceDocumentId is { } doc && view.EvidenceFileNames.TryGetValue(doc, out var name)
                        ? name
                        : null,
                    Notes = item?.Notes,
                };
            }).ToList(),
            MinimumExtinguishers = view.Evaluation.MinimumExtinguishers,
            IsComplete = view.Evaluation.IsComplete,
            Blockers = view.Evaluation.Blockers.Select(b => ToIssueDto(b, localizer)).ToList(),
            Warnings = view.Evaluation.Warnings.Select(w => ToIssueDto(w, localizer)).ToList(),
            ConfirmedAt = checklist?.ConfirmedAt,
            ConfirmedTextVersion = checklist?.ConfirmedTextVersion,
            UpdatedAt = checklist?.UpdatedAt,
        };
    }

    private static SafetyChecklistIssueDto ToIssueDto(SafetyChecklistIssue issue, IStringLocalizer<SharedResources> localizer) => new()
    {
        Code = issue.Code,
        Message = localizer[issue.MessageKey, issue.MessageArgs.ToArray()].Value,
    };

    // TN-3 resource-based check of a short-stay property for the compliance actions touched by CO-07: 404 when the
    // property is not visible (other org), 403 when visible but the operation is not allowed.
    private async Task<ActionResult?> AuthorizeShortStayPropertyAsync(Guid propertyId, HostOperationRequirement operation)
    {
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property is null)
            return NotFound();

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on compliance of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return Forbid();
        }

        return null;
    }

    /// <summary>403 of a create over the org's plan limit; the frontend branches on it (<c>isPlanLimitError</c>).</summary>
    internal const string PlanLimitReachedCode = "plan_limit_reached";

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException?.Message ?? string.Empty;
        return inner.Contains("23505", StringComparison.Ordinal)
            || inner.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}
