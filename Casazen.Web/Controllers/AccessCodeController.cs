using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Property access code management (NF-13, E06/F03/US01/T01).
/// Codes are encrypted at rest and NEVER released without verification.
/// </summary>
[ApiController]
[Route("api")]
public class AccessCodeController : ControllerBase
{
    private readonly IAccessCodeService _accessCodeService;

    public AccessCodeController(IAccessCodeService accessCodeService)
    {
        _accessCodeService = accessCodeService;
    }

    /// <summary>
    /// Sets the access code for a property (encrypted storage).
    /// Requires property.write permission.
    /// </summary>
    [HttpPost("properties/{id}/access-code")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetAccessCode(Guid id, [FromBody] SetAccessCodeRequest request)
    {
        var userId = User.FindFirst("sub")?.Value ?? string.Empty;

        await _accessCodeService.SetAccessCodeAsync(id, request.Code, userId);

        return Ok();
    }

    /// <summary>
    /// Gets the access code for a booking (verification required).
    /// Logs the access and blocks if verification not passed.
    /// Requires booking.read permission.
    /// </summary>
    [HttpGet("bookings/{id}/access-code")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(AccessCodeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccessCodeResponse>> GetAccessCode(Guid id)
    {
        var userId = User.FindFirst("sub")?.Value ?? string.Empty;

        var code = await _accessCodeService.GetAccessCodeAsync(id, userId);

        if (code == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "AccessCodeUnavailable", "Errors.AccessCodeUnavailable");

        return Ok(new AccessCodeResponse { Code = code });
    }
}

/// <summary>Request to set an access code.</summary>
public sealed record SetAccessCodeRequest
{
    /// <summary>Plain text access code (will be encrypted).</summary>
    public required string Code { get; init; }
}

/// <summary>Response containing the access code.</summary>
public sealed record AccessCodeResponse
{
    /// <summary>Plain text access code.</summary>
    public required string Code { get; init; }
}
