using System.Security.Claims;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// OTA partner API (Airbnb / Booking.com, #31-35): in freeze behind <see cref="FeatureFlags.OtaPartnerApi"/> (D10),
/// 404 while the flag is off. iCal import/export does not go through this controller.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
[FeatureGate(FeatureFlags.OtaPartnerApi)]
public class OtaController : ControllerBase
{
    private readonly IOtaManager _otaManager;
    private readonly IOtaIntegrationService _otaIntegrationService;
    private readonly IPropertyService _propertyService;
    private readonly IPropertyAuthorizationService _authorizationService;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<OtaController> _logger;

    public OtaController(
        IOtaManager otaManager,
        IOtaIntegrationService otaIntegrationService,
        IPropertyService propertyService,
        IPropertyAuthorizationService authorizationService,
        IBackgroundJobClient backgroundJobClient,
        ILogger<OtaController> logger)
    {
        _otaManager = otaManager;
        _otaIntegrationService = otaIntegrationService;
        _propertyService = propertyService;
        _authorizationService = authorizationService;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetIntegrations([FromQuery] Guid? propertyId)
    {
        try
        {
            if (!propertyId.HasValue)
                return Ok(Array.Empty<object>());

            var accessDenied = await EnsureCanAccessPropertyAsync(propertyId.Value);
            if (accessDenied is not null)
                return accessDenied;

            var integrations = await _otaIntegrationService.GetPropertyIntegrationsAsync(propertyId.Value);
            return Ok(integrations.Select(MapIntegration));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving OTA integrations for property {PropertyId}", propertyId);
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncAll([FromQuery] Guid propertyId)
    {
        try
        {
            var accessDenied = await EnsureCanAccessPropertyAsync(propertyId);
            if (accessDenied is not null)
                return accessDenied;

            // Queue job for background processing instead of blocking the request
            var jobId = _backgroundJobClient.Enqueue<OtaSyncJob>(job =>
                job.ExecuteAsync(propertyId));

            _logger.LogInformation("OTA sync job queued for property {PropertyId} with job ID {JobId}", propertyId, jobId);

            return Accepted(new
            {
                message = "Sync job queued",
                jobId,
                propertyId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queuing OTA sync job");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] Guid propertyId)
    {
        try
        {
            var accessDenied = await EnsureCanAccessPropertyAsync(propertyId);
            if (accessDenied is not null)
                return accessDenied;

            var status = await _otaManager.GetSyncStatusAsync(propertyId);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get status error");
            return StatusCode(500, "Internal server error");
        }
    }

    private async Task<IActionResult?> EnsureCanAccessPropertyAsync(Guid propertyId)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var property = await _propertyService.GetPropertyAsync(propertyId);
        if (property is null)
            return NotFound();

        if (!_authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
            return Forbid();

        return null;
    }

    private string? GetUserId() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private IEnumerable<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value);

    private OtaIntegrationDto MapIntegration(Casazen.Core.Entities.OtaIntegration integration) =>
        new()
        {
            Id = integration.Id,
            PropertyId = integration.PropertyId,
            Platform = integration.Platform,
            ExternalPropertyId = integration.ExternalPropertyId,
            ApiKeyMasked = _otaIntegrationService.MaskApiKey(integration.ApiKey),
            IsActive = integration.IsActive,
            LastSyncAt = integration.LastSyncAt,
            CreatedAt = integration.CreatedAt
        };
}
