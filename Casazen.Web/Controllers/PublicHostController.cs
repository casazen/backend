using System.ComponentModel.DataAnnotations;
using Casazen.Core.DTOs;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// F0 spike (#288) — resolve tenant from Host for Vercel edge middleware.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicHostController(IPublicHostResolver hostResolver) : ControllerBase
{
    [HttpGet("resolve-host")]
    [ProducesResponseType(typeof(ResolveHostResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResolveHostResponseDto>> ResolveHost(
        [FromQuery, Required, StringLength(253)] string host,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
            return BadRequest(new { error = "host query parameter is required" });

        var result = await hostResolver.ResolveAsync(host, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
