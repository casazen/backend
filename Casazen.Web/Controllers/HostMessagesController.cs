using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.GuestMessages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The host's list of message threads (DB-14a): <c>GET api/messages?needsReply=true</c> is the «Da rispondere» of the Home (open threads
/// whose last message is the guest's). What a caller sees follows its <see cref="HostScope"/> in SQL, so a collaborator reaches the
/// threads of its properties only, and a thread of another org is never listed.
/// </summary>
[ApiController]
[Route("api/messages")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class HostMessagesController(
    IGuestMessageService messages,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The threads of the properties the caller reaches, the latest message first, a page at a time; <c>needsReply</c> true keeps the open
    /// threads that wait for an answer (false the others), <c>propertyId</c> those of one property.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<HostThreadSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResultDto<HostThreadSummaryDto>>> List(
        [FromQuery] bool? needsReply,
        [FromQuery] Guid? propertyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = GuestMessageRules.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await messages.ListForHostAsync(scope, needsReply, propertyId, page, pageSize, cancellationToken);
        return Ok(new PagedResultDto<HostThreadSummaryDto>
        {
            Items = result.Items.Select(HostThreadSummaryDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
    }
}
