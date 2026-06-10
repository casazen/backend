using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Casazen.Web.Controllers;

/// <summary>
/// Manages AI-driven dynamic pricing configuration, history, and manual sync for properties.
/// </summary>
[ApiController]
[Route("api/pricing-adapter")]
[Authorize]
[SwaggerTag("Pricing Adapter")]
public class PricingAdapterController(
    IPricingAdapterService pricingService,
    IPropertyService propertyService,
    IPropertyAuthorizationService authorizationService,
    IBackgroundJobClient backgroundJobClient,
    ILogger<PricingAdapterController> logger) : ControllerBase
{
    /// <summary>
    /// Enable or update the AI pricing configuration for a property.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <param name="request">Pricing adapter configuration.</param>
    /// <response code="200">Configuration saved successfully.</response>
    /// <response code="400">Validation error in request body.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property not found.</response>
    [HttpPost("config/{propertyId:guid}")]
    [ProducesResponseType(typeof(PricingAdapterConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PricingAdapterConfigResponse>> SaveConfig(
        Guid propertyId, [FromBody] PricingAdapterConfigRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null) return NotFound();
        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
        {
            logger.LogWarning("User {UserId} attempted to update pricing config for property {PropertyId} owned by {OwnerId}",
                userId, propertyId, property.OwnerId);
            return Forbid();
        }

        var existing = await pricingService.GetConfigAsync(propertyId);
        var config = existing ?? new PricingAdapterConfig { Id = Guid.Empty, PropertyId = propertyId };

        config.IsEnabled = request.IsEnabled;
        config.AdaptationFrequency = request.AdaptationFrequency;
        config.IncludeSeasonality = request.IncludeSeasonality;
        config.IncludePublicHolidays = request.IncludePublicHolidays;

        if (request.IsEnabled && config.NextScheduledRunAt == null)
            config.NextScheduledRunAt = DateTime.UtcNow.AddDays(1);

        var saved = await pricingService.SaveConfigAsync(config);
        logger.LogInformation("Saved pricing config for property {PropertyId}", propertyId);

        return Ok(ToResponse(saved));
    }

    /// <summary>
    /// Get the current AI pricing configuration for a property.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <response code="200">Current configuration.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property or configuration not found.</response>
    [HttpGet("config/{propertyId:guid}")]
    [ProducesResponseType(typeof(PricingAdapterConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PricingAdapterConfigResponse>> GetConfig(Guid propertyId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null) return NotFound();
        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
        {
            logger.LogWarning("User {UserId} attempted to read pricing config for property {PropertyId} owned by {OwnerId}",
                userId, propertyId, property.OwnerId);
            return Forbid();
        }

        var config = await pricingService.GetConfigAsync(propertyId);
        if (config == null)
            return Ok(ToDefaultResponse(propertyId));

        return Ok(ToResponse(config));
    }

    /// <summary>
    /// Disable AI pricing for a property.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <response code="204">AI pricing disabled.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property or configuration not found.</response>
    [HttpDelete("config/{propertyId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisableConfig(Guid propertyId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null) return NotFound();
        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
        {
            logger.LogWarning("User {UserId} attempted to disable pricing config for property {PropertyId} owned by {OwnerId}",
                userId, propertyId, property.OwnerId);
            return Forbid();
        }

        var config = await pricingService.GetConfigAsync(propertyId);
        if (config == null) return NotFound();

        await pricingService.DisableConfigAsync(propertyId);
        logger.LogInformation("Disabled AI pricing for property {PropertyId}", propertyId);

        return NoContent();
    }

    /// <summary>
    /// Get paginated price change history for a property.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <param name="from">Start date (inclusive). Defaults to 90 days ago.</param>
    /// <param name="to">End date (inclusive). Defaults to today.</param>
    /// <param name="page">Page number (1-based). Defaults to 1.</param>
    /// <param name="pageSize">Items per page. Defaults to 50.</param>
    /// <response code="200">Paginated history.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property not found.</response>
    [HttpGet("history/{propertyId:guid}")]
    [ProducesResponseType(typeof(PricingHistoryPagedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PricingHistoryPagedResponse>> GetHistory(
        Guid propertyId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null) return NotFound();
        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
        {
            logger.LogWarning("User {UserId} attempted to read pricing history for property {PropertyId} owned by {OwnerId}",
                userId, propertyId, property.OwnerId);
            return Forbid();
        }

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 50;

        var startDate = from ?? DateTime.UtcNow.AddDays(-90);
        var endDate = to ?? DateTime.UtcNow;

        var (items, total) = await pricingService.GetHistoryPagedAsync(propertyId, startDate, endDate, page, pageSize);

        var response = new PricingHistoryPagedResponse
        {
            Items = items.Select(h => new PricingHistoryDto
            {
                Id = h.Id,
                PropertyId = h.PropertyId,
                AdaptationDate = h.AdaptationDate,
                PreviousPrice = h.PreviousPrice,
                NewPrice = h.NewPrice,
                ChangeReason = h.ChangeReason,
                AiConfidence = h.AiConfidence,
                OtasSynced = h.OtasSynced,
                SyncStatus = h.SyncStatus,
                CreatedAt = h.CreatedAt
            }),
            Total = total,
            Page = page
        };

        return Ok(response);
    }

    /// <summary>
    /// Trigger a manual one-off pricing sync for a property.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <response code="202">Sync job enqueued.</response>
    /// <response code="400">AI pricing is not enabled for this property.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property not found.</response>
    [HttpPost("sync/{propertyId:guid}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TriggerSync(Guid propertyId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null) return NotFound();
        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
        {
            logger.LogWarning("User {UserId} attempted to trigger pricing sync for property {PropertyId} owned by {OwnerId}",
                userId, propertyId, property.OwnerId);
            return Forbid();
        }

        var config = await pricingService.GetConfigAsync(propertyId);
        if (config == null || !config.IsEnabled)
            return BadRequest(new { error = "AI pricing is not enabled for this property. Enable it first via POST /config/{propertyId}." });

        var jobId = backgroundJobClient.Enqueue<DynamicPricingJob>(j => j.ExecuteForPropertyAsync(propertyId));
        logger.LogInformation("Enqueued manual pricing sync for property {PropertyId}, jobId={JobId}", propertyId, jobId);

        return Accepted(new { jobId });
    }

    /// <summary>
    /// Preview suggested prices for the next 90 days without persisting changes.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <response code="200">Preview of suggested daily prices.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property or configuration not found.</response>
    [HttpGet("preview/{propertyId:guid}")]
    [ProducesResponseType(typeof(PricingPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PricingPreviewResponse>> GetPreview(Guid propertyId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null) return NotFound();
        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
        {
            logger.LogWarning("User {UserId} attempted to preview pricing for property {PropertyId} owned by {OwnerId}",
                userId, propertyId, property.OwnerId);
            return Forbid();
        }

        var config = await pricingService.GetConfigAsync(propertyId);
        if (config == null || !config.IsEnabled)
        {
            return Ok(new PricingPreviewResponse { Prices = [] });
        }

        var preview = await pricingService.PreviewPricesAsync(propertyId, property.NightlyRate, config);

        var response = new PricingPreviewResponse
        {
            Prices = preview.Select(p => new PricingPreviewDayDto
            {
                Date = p.Date.ToString("yyyy-MM-dd"),
                SuggestedPrice = p.SuggestedPrice,
                BasePrice = p.BasePrice,
                Reason = p.Reason
            })
        };

        return Ok(response);
    }

    private string? GetUserId() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    private IEnumerable<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value);

    private static PricingAdapterConfigResponse ToDefaultResponse(Guid propertyId) => new()
    {
        PropertyId = propertyId,
        IsEnabled = false,
        AdaptationFrequency = "daily",
        IncludeSeasonality = true,
        IncludePublicHolidays = true,
    };

    private static PricingAdapterConfigResponse ToResponse(PricingAdapterConfig config) => new()
    {
        PropertyId = config.PropertyId,
        IsEnabled = config.IsEnabled,
        NextScheduledRunAt = config.NextScheduledRunAt,
        AdaptationFrequency = config.AdaptationFrequency,
        IncludeSeasonality = config.IncludeSeasonality,
        IncludePublicHolidays = config.IncludePublicHolidays,
        LastAdaptedAt = config.LastAdaptedAt,
        CreatedAt = config.CreatedAt,
        UpdatedAt = config.UpdatedAt
    };
}
