using Casazen.Core.HostInvites;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.HostInvites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/host-invites")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminHostInvitesController(IHostInviteService invites) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(HostInviteSentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostInviteSentDto>> Create([FromBody] CreateHostInviteRequest request, CancellationToken cancellationToken)
    {
        var sent = await invites.CreateAsync(
            new CreateHostInvite(Actor(), request.Email, request.Message, request.Locale), cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, HostInviteSentDto.From(sent));
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<HostInviteDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<HostInviteDto>>> List(
        [FromQuery] string? search,
        [FromQuery] string? state,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        HostInviteState? stateFilter = EnumNames.TryParseDefined<HostInviteState>(state, out var parsed) ? parsed : null;

        var (items, total) = await invites.ListAsync(new HostInviteListQuery(Actor(), search, stateFilter, page, pageSize), cancellationToken);
        return Ok(new PagedResultDto<HostInviteDto>
        {
            Items = items.Select(HostInviteDto.From).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpPost("{id:guid}/resend")]
    [ProducesResponseType(typeof(HostInviteSentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HostInviteSentDto>> Resend(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(HostInviteSentDto.From(await invites.ResendAsync(Actor(), id, cancellationToken)));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        await invites.RevokeAsync(Actor(), id, cancellationToken);
        return NoContent();
    }

    private HostInviteActor Actor() =>
        new(User.GetUserId() ?? throw new UnauthorizedAccessException("Admin without subject claim"), null, IsAdmin: true);
}
