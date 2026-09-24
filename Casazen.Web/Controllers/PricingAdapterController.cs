using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Casazen.Web.Controllers;

/// <summary>
/// Manages AI-driven dynamic pricing configuration, history, and manual sync for properties.
/// Host endpoints (TN-3): <c>property.read</c> to read, <c>property.write</c> to change; the property itself is
/// authorized as a <see cref="HostResource"/> (org, permission, ownership).
/// </summary>
[ApiController]
[Route("api/pricing-adapter")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
[SwaggerTag("Pricing Adapter")]
public class PricingAdapterController(
    IPricingAdapterService pricingService,
    IPropertyService propertyService,
    IAuthorizationService authorizationService,
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
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(PricingAdapterConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PricingAdapterConfigResponse>> SaveConfig(
        Guid propertyId, [FromBody] PricingAdapterConfigRequest request)
    {
        var (property, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null) return denied;

        var existing = await pricingService.GetConfigAsync(propertyId);
        var config = existing ?? new PricingAdapterConfig { Id = Guid.Empty, PropertyId = propertyId, OrgId = property.OrgId };

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
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Read);
        if (denied is not null) return denied;

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
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisableConfig(Guid propertyId)
    {
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null) return denied;

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
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Read);
        if (denied is not null) return denied;

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 50;

        // from/to arrive as UTC (FD-06). A date-only "to" (e.g. 2026-09-10) includes that whole day.
        var startDate = from ?? DateTime.UtcNow.AddDays(-90);
        var endDate = to switch
        {
            null => DateTime.UtcNow,
            { TimeOfDay.Ticks: 0 } endDay => endDay.AddDays(1).AddTicks(-1),
            { } endInstant => endInstant,
        };

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
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TriggerSync(Guid propertyId)
    {
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null) return denied;

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
        var (property, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Read);
        if (denied is not null) return denied;

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

    /// <summary>
    /// Loads the property (tenant-filtered: another org's property is 404) and authorizes <paramref name="operation"/>
    /// on it; a visible property the caller may not use is 403.
    /// </summary>
    private async Task<(Property Property, ActionResult? Denied)> AuthorizePropertyAsync(
        Guid propertyId,
        HostOperationRequirement operation)
    {
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null)
            return (null!, NotFound());

        if (!await authorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on pricing of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return (property, Forbid());
        }

        return (property, null);
    }

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
