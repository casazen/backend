using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Pricing;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Casazen.Web.Controllers;

/// <summary>
/// Seasonal price suggestions ("Suggerimenti stagionali", D4, PC-15): configuration, computed suggestions and manual
/// recalculation for a property. The suggestions are the property's nightly rate times the host's explicit rules; they are
/// read-only proposals (no price model per date exists yet): quotes and bookings keep using the nightly rate.
/// Host endpoints (TN-3): <c>property.read</c> to read, <c>property.write</c> to change; the property itself is
/// authorized as a <see cref="HostResource"/> (org, permission, ownership).
/// </summary>
[ApiController]
[Route("api/pricing-adapter")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
[SwaggerTag("Seasonal price suggestions")]
public class PricingAdapterController(
    IPricingAdapterService pricingService,
    IPropertyService propertyService,
    IAuthorizationService authorizationService,
    ILogger<PricingAdapterController> logger) : ControllerBase
{
    /// <summary>422: a recalculation was asked while the suggestions are disabled.</summary>
    public const string SuggestionsNotEnabledCode = "pricing_suggestions_not_enabled";

    /// <summary>
    /// Enable, disable or update the seasonal suggestions of a property. When enabled, the suggestions are recomputed
    /// right away with the saved rules; when disabled, the computed ones are removed.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <param name="request">Frequency and rules.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <response code="200">Configuration saved.</response>
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
        Guid propertyId, [FromBody] PricingAdapterConfigRequest request, CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null) return denied;

        var existing = await pricingService.GetConfigAsync(propertyId);
        var config = existing ?? new PricingAdapterConfig { Id = Guid.Empty, PropertyId = propertyId, OrgId = property.OrgId };

        config.IsEnabled = request.IsEnabled;
        config.AdaptationFrequency = request.AdaptationFrequency;
        config.IncludeSeasonality = request.IncludeSeasonality;
        config.IncludePublicHolidays = request.IncludePublicHolidays;
        if (request.HighSeasonMonths is not null) config.HighSeasonMonths = [.. request.HighSeasonMonths.Order()];
        if (request.LowSeasonMonths is not null) config.LowSeasonMonths = [.. request.LowSeasonMonths.Order()];
        if (request.HighSeasonMultiplier is { } high) config.HighSeasonMultiplier = high;
        if (request.LowSeasonMultiplier is { } low) config.LowSeasonMultiplier = low;
        if (request.HolidayMultiplier is { } holiday) config.HolidayMultiplier = holiday;

        // Only one list sent: it may overlap the stored other one.
        if (config.HighSeasonMonths.Intersect(config.LowSeasonMonths).Any())
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "PricingSeasonMonthsOverlap");

        var saved = await pricingService.SaveConfigAsync(config);
        if (saved.IsEnabled)
            await pricingService.RegenerateSuggestionsAsync(propertyId, onlyIfDue: false, cancellationToken);
        else
            await pricingService.DisableConfigAsync(propertyId, cancellationToken);

        logger.LogInformation("Saved seasonal price suggestions config for property {PropertyId}", propertyId);
        return Ok(PricingAdapterConfigResponse.From(saved));
    }

    /// <summary>
    /// Get the seasonal suggestions configuration of a property (the example rule, disabled, when none was saved).
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <response code="200">Current configuration.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property not found.</response>
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
        return Ok(config is null ? PricingAdapterConfigResponse.Default(propertyId) : PricingAdapterConfigResponse.From(config));
    }

    /// <summary>
    /// Disable the seasonal suggestions of a property and remove the computed ones.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <response code="204">Suggestions disabled.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property or configuration not found.</response>
    [HttpDelete("config/{propertyId:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisableConfig(Guid propertyId, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null) return denied;

        var config = await pricingService.GetConfigAsync(propertyId);
        if (config == null) return NotFound();

        await pricingService.DisableConfigAsync(propertyId, cancellationToken);
        logger.LogInformation("Disabled seasonal price suggestions for property {PropertyId}", propertyId);

        return NoContent();
    }

    /// <summary>
    /// The computed seasonal suggestions of a property (one per date, 90 days from the last computation), with the
    /// property's current nightly rate. Empty when the suggestions are disabled or not computed yet.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <response code="200">Suggestions.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property not found.</response>
    [HttpGet("suggestions/{propertyId:guid}")]
    [ProducesResponseType(typeof(SeasonalSuggestionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeasonalSuggestionsResponse>> GetSuggestions(
        Guid propertyId, CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Read);
        if (denied is not null) return denied;

        var config = await pricingService.GetConfigAsync(propertyId);
        var enabled = config is { IsEnabled: true };
        var items = enabled ? await pricingService.GetSuggestionsAsync(propertyId, cancellationToken) : [];

        return Ok(new SeasonalSuggestionsResponse
        {
            IsEnabled = enabled,
            CurrentBasePrice = property.NightlyRate,
            ComputedAt = enabled ? config!.LastAdaptedAt : null,
            NextRunOn = enabled ? SeasonalSuggestionSchedule.NextRunOn(config!.AdaptationFrequency, config.LastAdaptedAt) : null,
            Items = [.. items.Select(SeasonalSuggestionDto.From)],
        });
    }

    /// <summary>
    /// Recompute the seasonal suggestions of a property now, with the same logic as the nightly job (one row per date,
    /// updated in place).
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <response code="200">Recalculated (or <c>BasePriceMissing</c> when the property has no nightly rate).</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="403">Caller is not the property owner.</response>
    /// <response code="404">Property not found.</response>
    /// <response code="422">The suggestions are not enabled for this property.</response>
    [HttpPost("recalculate/{propertyId:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(SeasonalSuggestionRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SeasonalSuggestionRunResponse>> Recalculate(
        Guid propertyId, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null) return denied;

        var result = await pricingService.RegenerateSuggestionsAsync(propertyId, onlyIfDue: false, cancellationToken);
        if (result.Status == SeasonalSuggestionRunStatus.NotEnabled)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity, SuggestionsNotEnabledCode, "PricingSuggestionsNotEnabled");
        }

        logger.LogInformation(
            "Recalculated seasonal price suggestions for property {PropertyId}: {Status}", propertyId, result.Status);
        return Ok(new SeasonalSuggestionRunResponse
        {
            Status = result.Status,
            Days = result.Days,
            ComputedAt = result.ComputedAt,
        });
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
}
