using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/gdpr")]
[Authorize]
public class GdprController(IGdprService gdprService, ILogger<GdprController> logger) : ControllerBase
{
    /// <summary>GDPR Article 15 - Right of Access: export all personal data for a guest.</summary>
    [HttpGet("guests/{id}/export")]
    public async Task<IActionResult> ExportGuestData(Guid id)
    {
        logger.LogInformation("GDPR data export requested for guest {GuestId}", id);
        var data = await gdprService.ExportGuestDataAsync(id);
        return Ok(data);
    }

    /// <summary>GDPR Article 17 - Right to Erasure: delete and anonymize guest data.</summary>
    [HttpDelete("guests/{id}")]
    public async Task<IActionResult> DeleteGuestData(Guid id, [FromQuery] string reason = "User request")
    {
        logger.LogInformation("GDPR deletion requested for guest {GuestId}", id);
        await gdprService.DeleteGuestDataAsync(id, reason);
        return NoContent();
    }

    /// <summary>Anonymize guest personal data while preserving booking records.</summary>
    [HttpPost("guests/{id}/anonymize")]
    public async Task<IActionResult> AnonymizeGuestData(Guid id)
    {
        logger.LogInformation("GDPR anonymization requested for guest {GuestId}", id);
        await gdprService.AnonymizeGuestDataAsync(id);
        return NoContent();
    }

    /// <summary>Update guest marketing consent status.</summary>
    [HttpPut("guests/{id}/consent")]
    public async Task<IActionResult> UpdateConsent(Guid id, [FromBody] UpdateConsentRequest request)
    {
        await gdprService.UpdateConsentAsync(id, request.MarketingConsent);
        return NoContent();
    }
}

public record UpdateConsentRequest(bool MarketingConsent);
