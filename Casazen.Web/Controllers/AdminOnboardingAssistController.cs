using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The staff queue of the «Do it for me» requests (UX-15, NF-15, E10/F01/US03/T01): CasaZen support reads the requests of every org and takes,
/// completes or closes them. Every update writes the line <c>OnboardingAssistUpdated</c> in the log of the org <b>of the request</b>, with the staff
/// user as the actor (AC-NF15-3).
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.AdminOnly"/>, the platform administrators; no host policy opens these routes.</para>
/// <para><b>What staff sees.</b> Ids, context codes, the property and its comune, the status and the note: never the draft, never the person who
/// asked. A credential only through the controlled opening of the support vault (<see cref="Reveal"/>, E20/F03/US01/T01): on an open request, with
/// a reason, at most five times an hour, audited and told to the host.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/admin/onboarding/assist-requests")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminOnboardingAssistController(IOnboardingAssistService assist, ISupportVaultService vault) : ControllerBase
{
    /// <summary>The queue, a page at a time, in SQL: open requests first and the oldest first.</summary>
    /// <param name="status">Narrows to one status: <c>Requested</c>, <c>InProgress</c>, <c>Done</c> or <c>Cancelled</c>; all when it is left out.</param>
    /// <param name="page">From 1.</param>
    /// <param name="pageSize">At most 100; 25 when it is left out.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <response code="200">A page of requests.</response>
    /// <response code="422"><c>assist_status_invalid</c>: the status is not one of the four.</response>
    [HttpGet]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PagedResultDto<AdminAssistRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResultDto<AdminAssistRequestDto>>> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = OnboardingAssistLimits.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        OnboardingAssistStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!EnumNames.TryParseDefined<OnboardingAssistStatus>(status, out var parsed))
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, OnboardingAssistCodes.StatusInvalid, "AssistStatusInvalid");

            filter = parsed;
        }

        return Ok((await assist.ListQueueAsync(filter, page, pageSize, cancellationToken)).ToDto());
    }

    /// <summary>
    /// One request with the checklist of what support needs, per property (E20/F03/US01/T02): the user of the panel in the vault, the police and the ISTAT
    /// credentials (and whether the region needs the ISTAT ones), the sender of the property, and the panel steps to do with the key of their guide and
    /// their state. Presence and dates only: never a credential, never the draft.
    /// </summary>
    /// <response code="200">The request and its checklist.</response>
    /// <response code="404"><c>assist_request_not_found</c>.</response>
    [HttpGet("{id:guid}")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(AdminAssistRequestDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminAssistRequestDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok((await assist.GetAsync(id, cancellationToken)).ToDto());

    /// <summary>
    /// Takes (<c>inProgress</c>), completes (<c>done</c>) or closes (<c>cancelled</c>) a request, with an optional note. <c>done</c> on a panel
    /// context first reads the panel for the org of the request (E20/F03/US01/T02): the step becomes <c>verified</c> when CasaZen read it done (the
    /// police: <c>policeAccount.valid</c>), otherwise <c>confirmed</c> as declared by the staff user; the request records <c>verified</c> or
    /// <c>declared</c>, the host's "To do" is closed and the administrators of the org are told live.
    /// </summary>
    /// <response code="200">The request as staff reads it.</response>
    /// <response code="404"><c>assist_request_not_found</c>.</response>
    /// <response code="409"><c>assist_request_closed</c>: the request is done or cancelled already; <c>assist_done_not_valid</c>: the panel reads the step as
    /// not valid (nothing changed). A read of the panel that failed answers with the code of the channel manager (nothing changed).</response>
    /// <response code="422"><c>assist_status_invalid</c>, <c>assist_note_invalid</c> (over 500 characters, or an e-mail address in it).</response>
    [HttpPut("{id:guid}")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(AdminAssistRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminAssistRequestDto>> Update(
        Guid id,
        [FromBody] UpdateAssistRequestDto? body,
        CancellationToken cancellationToken)
    {
        var staffUserId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(staffUserId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (body?.Status is not { } status)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, OnboardingAssistCodes.StatusInvalid, "AssistStatusInvalid");

        return Ok((await assist.UpdateAsync(staffUserId, id, status, body.Note, cancellationToken)).ToDto());
    }

    /// <summary>
    /// The controlled opening of the support vault (E20/F03/US01/T01, decision O5): the values of one credential (<c>OctoratePanel</c>, the user of
    /// the panel of the structure; <c>Police</c>, the Alloggiati Web credentials of the property; <c>RegionalIstat</c>, its ISTAT credentials), in clear,
    /// once, with <c>Cache-Control: no-store</c>, for an open request of the same org and property only. Every opening writes the audit (who, when,
    /// request, kind, reason; never the value) and the line of the log the host reads.
    /// </summary>
    /// <response code="200">The values, and the end of the minute the client shows them for.</response>
    /// <response code="404"><c>assist_request_not_found</c>, <c>support_vault_secret_missing</c>.</response>
    /// <response code="409"><c>support_vault_request_not_open</c>: the request is done or cancelled.</response>
    /// <response code="422"><c>support_vault_kind_invalid</c>, <c>support_vault_kind_not_for_request</c>, <c>support_vault_reason_invalid</c> (10 to 300 characters).</response>
    /// <response code="429"><c>support_vault_too_many_openings</c>: five openings of the request in the last hour.</response>
    [HttpPost("{id:guid}/reveal")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(SupportVaultRevealDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SupportVaultRevealDto>> Reveal(
        Guid id,
        [FromBody] RevealSupportVaultDto? body,
        CancellationToken cancellationToken)
    {
        var staffUserId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(staffUserId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (body?.Kind is not { } kind)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, SupportVaultCodes.KindInvalid, "SupportVaultKindInvalid");

        return Ok(SupportVaultRevealDto.From(await vault.RevealAsync(staffUserId, id, kind, body.Reason, cancellationToken)));
    }
}
