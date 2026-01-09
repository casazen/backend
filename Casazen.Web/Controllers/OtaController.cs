using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OtaController(IOtaManager otaManager, ILogger<OtaController> logger) : ControllerBase
{
    [HttpPost("sync")]
    public async Task<IActionResult> SyncAll([FromQuery] Guid propertyId)
    {
        try
        {
            var success = await otaManager.SyncAllAsync(propertyId);
            return success ? Ok(new { message = "Sync completed successfully" }) : BadRequest("Sync failed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OTA sync error");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("sync-platform")]
    public async Task<IActionResult> SyncPlatform([FromQuery] string platform, [FromQuery] string externalId)
    {
        try
        {
            var success = await otaManager.SyncPlatformAsync(platform, externalId);
            return success ? Ok(new { message = $"{platform} sync completed" }) : BadRequest("Sync failed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Platform {platform} sync error");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] Guid propertyId)
    {
        try
        {
            var status = await otaManager.GetSyncStatusAsync(propertyId);
            return Ok(status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get status error");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPut("pricing")]
    public async Task<IActionResult> UpdatePricing([FromQuery] Guid propertyId, [FromQuery] decimal newPrice)
    {
        try
        {
            var success = await otaManager.UpdatePricingAsync(propertyId, newPrice);
            return success ? Ok(new { message = "Pricing updated" }) : BadRequest("Update failed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pricing update error");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("validate")]
    public async Task<IActionResult> ValidateIntegration([FromQuery] string platform, [FromQuery] string apiKey)
    {
        try
        {
            var success = await otaManager.ValidateIntegrationAsync(platform, apiKey);
            return success ? Ok(new { valid = true }) : Ok(new { valid = false });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validation error");
            return StatusCode(500, "Internal server error");
        }
    }
}
