using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{propertyId}/ota-integrations")]
[Authorize(Policy = "PropertyOwner")]
public class OtaIntegrationsController(
    IOtaIntegrationService otaIntegrationService,
    ILogger<OtaIntegrationsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<OtaIntegrationDto>>> GetAll(Guid propertyId)
    {
        logger.LogInformation("Getting OTA integrations for property {PropertyId}", propertyId);
        var integrations = await otaIntegrationService.GetPropertyIntegrationsAsync(propertyId);

        var dtos = integrations.Select(i => new OtaIntegrationDto
        {
            Id = i.Id,
            PropertyId = i.PropertyId,
            Platform = i.Platform,
            ExternalPropertyId = i.ExternalPropertyId,
            ApiKeyMasked = otaIntegrationService.MaskApiKey(i.ApiKey),
            IsActive = i.IsActive,
            LastSyncAt = i.LastSyncAt,
            CreatedAt = i.CreatedAt
        });

        return Ok(dtos);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<OtaIntegrationDto>> Get(Guid propertyId, Guid id)
    {
        logger.LogInformation("Getting OTA integration {Id} for property {PropertyId}", id, propertyId);
        var integration = await otaIntegrationService.GetIntegrationAsync(id);

        if (integration == null || integration.PropertyId != propertyId)
            return NotFound();

        var dto = new OtaIntegrationDto
        {
            Id = integration.Id,
            PropertyId = integration.PropertyId,
            Platform = integration.Platform,
            ExternalPropertyId = integration.ExternalPropertyId,
            ApiKeyMasked = otaIntegrationService.MaskApiKey(integration.ApiKey),
            IsActive = integration.IsActive,
            LastSyncAt = integration.LastSyncAt,
            CreatedAt = integration.CreatedAt
        };

        return Ok(dto);
    }

    [HttpPost]
    public async Task<ActionResult<OtaIntegrationDto>> Create(
        Guid propertyId,
        [FromBody] CreateOtaIntegrationRequest request)
    {
        if (request.PropertyId != propertyId)
            return BadRequest("Property ID in URL must match request body");

        logger.LogInformation("Creating OTA integration for property {PropertyId}, platform {Platform}",
            propertyId, request.Platform);

        var integration = await otaIntegrationService.CreateIntegrationAsync(
            propertyId,
            request.Platform,
            request.ExternalPropertyId,
            request.ApiKey);

        var dto = new OtaIntegrationDto
        {
            Id = integration.Id,
            PropertyId = integration.PropertyId,
            Platform = integration.Platform,
            ExternalPropertyId = integration.ExternalPropertyId,
            ApiKeyMasked = otaIntegrationService.MaskApiKey(integration.ApiKey),
            IsActive = integration.IsActive,
            LastSyncAt = integration.LastSyncAt,
            CreatedAt = integration.CreatedAt
        };

        return CreatedAtAction(nameof(Get), new { propertyId, id = dto.Id }, dto);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(
        Guid propertyId,
        Guid id,
        [FromBody] UpdateOtaIntegrationRequest request)
    {
        logger.LogInformation("Updating OTA integration {Id} for property {PropertyId}", id, propertyId);

        var existing = await otaIntegrationService.GetIntegrationAsync(id);
        if (existing == null || existing.PropertyId != propertyId)
            return NotFound();

        await otaIntegrationService.UpdateIntegrationAsync(
            id,
            request.ExternalPropertyId,
            request.ApiKey,
            request.IsActive);

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid propertyId, Guid id)
    {
        logger.LogInformation("Deleting OTA integration {Id} for property {PropertyId}", id, propertyId);

        var existing = await otaIntegrationService.GetIntegrationAsync(id);
        if (existing == null || existing.PropertyId != propertyId)
            return NotFound();

        await otaIntegrationService.DeleteIntegrationAsync(id);

        return NoContent();
    }
}
