using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/gdpr")]
[Authorize]
public class GdprController(
    IGdprService gdprService,
    IOrgContextResolver orgContextResolver,
    IGuestAccessService guestAccessService,
    ILogger<GdprController> logger) : ControllerBase
{
    [HttpGet("guests/{id}/export")]
    [Authorize(Policy = "RequireContext:short-rent:guest.read")]
    public async Task<IActionResult> ExportGuestData(Guid id)
    {
        if (!await EnsureGuestAccessibleAsync(id))
            return NotFound();

        logger.LogInformation("GDPR data export requested for guest {GuestId}", id);
        var data = await gdprService.ExportGuestDataAsync(id);
        return Ok(data);
    }

    [HttpDelete("guests/{id}")]
    [Authorize(Policy = "RequireContext:short-rent:guest.write")]
    public async Task<IActionResult> DeleteGuestData(Guid id, [FromQuery] string reason = "User request")
    {
        if (!await EnsureGuestAccessibleAsync(id))
            return NotFound();

        logger.LogInformation("GDPR deletion requested for guest {GuestId}", id);
        await gdprService.DeleteGuestDataAsync(id, reason);
        return NoContent();
    }

    [HttpPost("guests/{id}/anonymize")]
    [Authorize(Policy = "RequireContext:short-rent:guest.write")]
    public async Task<IActionResult> AnonymizeGuestData(Guid id)
    {
        if (!await EnsureGuestAccessibleAsync(id))
            return NotFound();

        logger.LogInformation("GDPR anonymization requested for guest {GuestId}", id);
        await gdprService.AnonymizeGuestDataAsync(id);
        return NoContent();
    }

    [HttpPut("guests/{id}/consent")]
    [Authorize(Policy = "RequireContext:short-rent:guest.write")]
    public async Task<IActionResult> UpdateConsent(Guid id, [FromBody] UpdateConsentRequest request)
    {
        if (!await EnsureGuestAccessibleAsync(id))
            return NotFound();

        await gdprService.UpdateConsentAsync(id, request.MarketingConsent);
        return NoContent();
    }

    [HttpGet("org/export")]
    [Authorize(Policy = "RequireContext:short-rent:property.read")]
    public async Task<IActionResult> ExportOrgFiscal(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NotFound();
        var data = await gdprService.ExportOrgFiscalDataAsync(orgId.Value, cancellationToken);
        return Ok(data);
    }

    [HttpPost("org/anonymize")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    public async Task<IActionResult> AnonymizeOrgFiscal(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NotFound();
        await gdprService.AnonymizeOrgFiscalDataAsync(orgId.Value, cancellationToken);
        return NoContent();
    }

    private async Task<bool> EnsureGuestAccessibleAsync(Guid guestId)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return false;

        return await guestAccessService.IsGuestAccessibleAsync(guestId, orgId.Value, HttpContext.RequestAborted);
    }
}

public record UpdateConsentRequest(bool MarketingConsent);
