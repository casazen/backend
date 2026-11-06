using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.GuestMessages;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The host's list of message threads (DB-14a): <c>GET api/messages?needsReply=true</c> is the «Da rispondere» of the Home (open threads
/// whose last message is the guest's). What a caller sees follows its <see cref="HostScope"/> in SQL, so a collaborator reaches the
/// threads of its properties only, and a thread of another org is never listed. With <c>includeOta=true</c> the same endpoints answer the hub of
/// messages (E05/F02/US01/T01): the conversations of the channels and the direct ones in one list; without it they answer exactly as before.
/// </summary>
/// <remarks>The hub service is an optional constructor parameter at the end, so a test that builds the controller for the direct threads is untouched.</remarks>
[ApiController]
[Route("api/messages")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
[PrivateAnswer]
public class HostMessagesController(
    IGuestMessageService messages,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IMessageHubService? hub = null) : ControllerBase
{
    /// <summary>
    /// The threads of the properties the caller reaches, the latest message first, a page at a time; <c>needsReply</c> true keeps the open
    /// threads that wait for an answer (false the others), <c>propertyId</c> those of one property. With <c>includeOta=true</c> the answer is a page of
    /// <see cref="MessageHubThreadDto"/>, the conversations of the channels and the direct ones together, which the filters <c>channel</c> (<c>Direct</c>,
    /// <c>Airbnb</c>, <c>Booking</c>, <c>Vrbo</c>, <c>Expedia</c>), <c>bookingId</c>, <c>unread</c>, <c>kind</c> (<c>Reservation</c>, <c>Request</c>) and
    /// <c>q</c> (the name of the guest, the booking code or the name of the property; not the words of a message) narrow further. Those five are
    /// read only with <c>includeOta=true</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<HostThreadSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PagedResultDto<MessageHubThreadDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResultDto<HostThreadSummaryDto>>> List(
        [FromQuery] bool? needsReply,
        [FromQuery] Guid? propertyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = GuestMessageRules.DefaultPageSize,
        CancellationToken cancellationToken = default,
        [FromQuery] bool includeOta = false,
        [FromQuery] MessageHubChannel? channel = null,
        [FromQuery] Guid? bookingId = null,
        [FromQuery] bool? unread = null,
        [FromQuery] MessageHubThreadKind? kind = null,
        [FromQuery] string? q = null)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (includeOta)
        {
            var filter = new MessageHubFilter(needsReply, propertyId, channel, bookingId, unread, kind, q);
            var hubPage = await Hub().ListAsync(scope, filter, page, pageSize, cancellationToken);
            return Ok(new PagedResultDto<MessageHubThreadDto>
            {
                Items = hubPage.Items.Select(MessageHubThreadDto.From).ToList(),
                TotalCount = hubPage.TotalCount,
                Page = hubPage.Page,
                PageSize = hubPage.PageSize,
            });
        }

        var result = await messages.ListForHostAsync(scope, needsReply, propertyId, page, pageSize, cancellationToken);
        return Ok(new PagedResultDto<HostThreadSummaryDto>
        {
            Items = result.Items.Select(HostThreadSummaryDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
    }

    /// <summary>
    /// The open threads with messages of the guest the host has not read, one item per booking with its <c>unreadCount</c>, for the badges of
    /// the calendar and the lists (at most 200, the latest first). Same permission and scope as the list; never the text of a message. With
    /// <c>includeOta=true</c> the answer is <see cref="MessageHubUnreadDto"/>: one item per conversation, the direct ones and then the ones of the
    /// channels (at most 200 of each), each with its <c>threadKey</c>, the booking it is about when there is one, and its <c>unreadCount</c>.
    /// </summary>
    [HttpGet("unread")]
    [ProducesResponseType(typeof(HostUnreadThreadsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MessageHubUnreadDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<HostUnreadThreadsDto>> Unread(CancellationToken cancellationToken, [FromQuery] bool includeOta = false)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (includeOta)
            return Ok(MessageHubUnreadDto.From(await Hub().ListUnreadAsync(scope, includeOta: true, cancellationToken)));

        return Ok(HostUnreadThreadsDto.From(await messages.ListUnreadForHostAsync(scope, cancellationToken)));
    }

    private IMessageHubService Hub() =>
        hub ?? throw new InvalidOperationException("The hub of messages is not registered (AddMessagesHub).");
}
