using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
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
    private readonly IOrgContextResolver _orgContextResolver;
    private readonly IHostScopeResolver _hostScopeResolver;

    public AccessCodeController(
        IAccessCodeService accessCodeService,
        IOrgContextResolver orgContextResolver,
        IHostScopeResolver hostScopeResolver)
    {
        _accessCodeService = accessCodeService;
        _orgContextResolver = orgContextResolver;
        _hostScopeResolver = hostScopeResolver;
    }

    /// <summary>
    /// Which of the properties the caller reaches have a lock code (NF-50, E03/F01/US03/T01): for each one <c>accessCodeSet</c> and
    /// <c>updatedAt</c>, a page at a time (<c>{ items, totalCount, page, pageSize }</c>, <c>page</c> from 1, <c>pageSize</c> 50 when
    /// missing and at most 100). The properties are bound in SQL to the caller's org and to what it reaches (<c>HostScope</c>, AM-03),
    /// so a collaborator «Solo alcuni» sees only the properties it was given. The answer never contains the code, in any form, and the
    /// query does not read the encrypted column: the code is released to a guest after a passed verification only (ADR-013).
    /// Requires property.read (short-rent context).
    /// </summary>
    [HttpGet("properties/access-codes")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PagedResultDto<AccessCodeStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResultDto<AccessCodeStatusDto>>> GetStatus(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AccessCodeStatusPage.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var orgId = await _orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await _hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await _accessCodeService.GetStatusAsync(scope, page, pageSize, cancellationToken);
        return Ok(new PagedResultDto<AccessCodeStatusDto>
        {
            Items = result.Items.Select(AccessCodeStatusDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
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
