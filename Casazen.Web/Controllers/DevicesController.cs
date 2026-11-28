using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs.Devices;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/devices")]
[Authorize]
public class DevicesController(
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(DeviceRegistrationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DeviceRegistrationDto>> Register(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken)
            ?? await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.OnboardingRequired, "OnboardingRequired");

        var platform = request.Platform.Trim().ToLowerInvariant();
        if (platform is not ("ios" or "android"))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "DevicePlatformInvalid");

        var deviceId = request.DeviceId.Trim();
        var pushToken = request.PushToken.Trim();
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(pushToken))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "DeviceFieldsRequired");

        DeviceRegistration registration;
        try
        {
            registration = await UpsertAsync(userId, orgId.Value, platform, deviceId, pushToken, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsConcurrentRegistration(ex))
        {
            // The same installation (or push token) registered by a concurrent request, e.g. at app start and after the
            // permission grant: the loser re-reads the rows written by the winner and applies its registration again.
            db.ChangeTracker.Clear();
            registration = await UpsertAsync(userId, orgId.Value, platform, deviceId, pushToken, cancellationToken);
        }

        return CreatedAtAction(nameof(Register), new { id = registration.Id }, Map(registration));
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

        var id = deviceId.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).Trim();

        var registration = await db.DeviceRegistrations
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == id, cancellationToken);

        if (registration is null)
            return NotFound();

        db.DeviceRegistrations.Remove(registration);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<DeviceRegistration> UpsertAsync(
        string userId,
        Guid orgId,
        string platform,
        string deviceId,
        string pushToken,
        CancellationToken cancellationToken)
    {
        var staleRegistrations = await db.DeviceRegistrations
            .Where(d => d.PushToken == pushToken && (d.UserId != userId || d.DeviceId != deviceId))
            .ToListAsync(cancellationToken);
        if (staleRegistrations.Count > 0)
            db.DeviceRegistrations.RemoveRange(staleRegistrations);

        var registration = await db.DeviceRegistrations
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, cancellationToken);

        if (registration is not null)
        {
            registration.PushToken = pushToken;
            registration.Platform = platform;
            registration.OrgId = orgId;
            registration.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            registration = new DeviceRegistration
            {
                UserId = userId,
                OrgId = orgId,
                Platform = platform,
                PushToken = pushToken,
                DeviceId = deviceId,
            };
            db.DeviceRegistrations.Add(registration);
        }

        await db.SaveChangesAsync(cancellationToken);
        return registration;
    }

    /// <summary>23505 on (UserId, DeviceId), or a stale row already removed by a concurrent registration.</summary>
    private static bool IsConcurrentRegistration(DbUpdateException ex) =>
        ex is DbUpdateConcurrencyException
        || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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
