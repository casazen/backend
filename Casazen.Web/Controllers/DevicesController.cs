using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs.Devices;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/devices")]
[Authorize]
public class DevicesController(
    AppDbContext db,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(DeviceRegistrationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeviceRegistrationDto>> Register(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (userId is null || orgId is null)
            return Unauthorized();

        var platform = request.Platform.Trim().ToLowerInvariant();
        if (platform is not ("ios" or "android"))
            return BadRequest(new { error = "Platform must be ios or android." });

        var deviceId = request.DeviceId.Trim();
        var pushToken = request.PushToken.Trim();
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(pushToken))
            return BadRequest(new { error = "DeviceId and PushToken are required." });

        var existing = await db.DeviceRegistrations
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, cancellationToken);

        if (existing is not null)
        {
            existing.PushToken = pushToken;
            existing.Platform = platform;
            existing.OrgId = orgId.Value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new DeviceRegistration
            {
                UserId = userId,
                OrgId = orgId.Value,
                Platform = platform,
                PushToken = pushToken,
                DeviceId = deviceId,
            };
            db.DeviceRegistrations.Add(existing);
        }

        await db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Register), new { id = existing.Id }, Map(existing));
    }

    [HttpDelete("{deviceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unregister(string deviceId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var registration = await db.DeviceRegistrations
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, cancellationToken);

        if (registration is null)
            return NotFound();

        db.DeviceRegistrations.Remove(registration);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private string? GetUserId() =>
        User.FindFirstValue("sub")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");

    private static DeviceRegistrationDto Map(DeviceRegistration d) => new()
    {
        Id = d.Id,
        Platform = d.Platform,
        DeviceId = d.DeviceId,
        UpdatedAt = d.UpdatedAt,
    };
}
