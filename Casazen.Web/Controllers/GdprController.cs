using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GdprController(
    IGdprService gdprService,
    ILogger<GdprController> logger) : ControllerBase
{
    /// <summary>
    /// Request data erasure (GDPR Article 17 - Right to be forgotten)
    /// </summary>
    [HttpPost("erasure-request")]
    public async Task<IActionResult> RequestErasure([FromBody] ErasureRequest request)
    {
        try
        {
            var success = await gdprService.RequestDataErasureAsync(request.GuestId, request.Reason);

            if (!success)
                return NotFound($"Guest {request.GuestId} not found");

            logger.LogInformation(
                "GDPR erasure requested by user {UserId} for guest {GuestId}",
                User.FindFirst("sub")?.Value, request.GuestId);

            return Ok(new
            {
                message = "Erasure request submitted successfully. " +
                         "Data will be anonymized within 30 days as per GDPR requirements.",
                guestId = request.GuestId,
                requestedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing erasure request for guest {GuestId}", request.GuestId);
            return StatusCode(500, new { error = "Failed to process erasure request" });
        }
    }

    /// <summary>
    /// Anonymize guest data (Admin only - execute erasure)
    /// </summary>
    [HttpPost("anonymize/{guestId}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AnonymizeData(Guid guestId)
    {
        try
        {
            var success = await gdprService.AnonymizeGuestDataAsync(guestId);

            if (!success)
                return NotFound($"Guest {guestId} not found");

            logger.LogInformation(
                "Guest data anonymized by admin {UserId} for guest {GuestId}",
                User.FindFirst("sub")?.Value, guestId);

            return Ok(new
            {
                message = "Guest data anonymized successfully. GDPR Article 17 compliance completed.",
                guestId,
                anonymizedAt = DateTime.UtcNow
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Cannot anonymize guest {GuestId}: {Error}", guestId, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error anonymizing guest {GuestId}", guestId);
            return StatusCode(500, new { error = "Failed to anonymize guest data" });
        }
    }

    /// <summary>
    /// Get pending erasure requests (Admin only)
    /// </summary>
    [HttpGet("pending-erasures")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GetPendingErasures()
    {
        var pendingRequests = await gdprService.GetPendingErasureRequestsAsync();

        return Ok(pendingRequests.Select(g => new
        {
            g.Id,
            g.Email,
            g.FirstName,
            g.LastName,
            g.ErasureRequestedDate,
            DaysSinceRequest = g.ErasureRequestedDate.HasValue
                ? (DateTime.UtcNow - g.ErasureRequestedDate.Value).Days
                : 0
        }));
    }

    /// <summary>
    /// Export guest data (GDPR Article 15 - Right to access)
    /// </summary>
    [HttpGet("export/{guestId}")]
    public async Task<IActionResult> ExportData(Guid guestId)
    {
        try
        {
            // Verify user has access to this guest's data
            var userId = User.FindFirst("sub")?.Value;
            // TODO: Add authorization check to verify user owns this data

            var export = await gdprService.ExportGuestDataAsync(guestId);

            logger.LogInformation(
                "Guest data exported by user {UserId} for guest {GuestId}",
                userId, guestId);

            return Ok(export);
        }
        catch (KeyNotFoundException)
        {
            return NotFound($"Guest {guestId} not found");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error exporting data for guest {GuestId}", guestId);
            return StatusCode(500, new { error = "Failed to export guest data" });
        }
    }
}

public record ErasureRequest(Guid GuestId, string Reason);
